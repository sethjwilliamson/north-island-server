using Microsoft.Extensions.Logging;
using NorthIsland.Protocol.IO;
using NorthIsland.Protocol.Messages.Items;
using NorthIsland.Protocol.Messages.Player;

namespace NorthIsland.Server.Features.Storage;

/// <summary>Serves the bank: items stored per character in a bank grid, and a bank gold balance next to the purse.</summary>
/// <param name="items">The item records, for stack limits, item sizes and flags.</param>
/// <param name="logger">The logger.</param>
public sealed class BankHandlers(
    ItemData items,
    ILogger<BankHandlers> logger)
{
    /// <summary>The most gold a purse can hold, carried or in the bank (9,999,999).</summary>
    public const int MaxGold = 9_999_999;

    /// <summary>
    /// Opens the bank's item window: records it as open on the player's map (<see cref="OpenServices"/>), sends the stored items and the balance,
    /// then shows the window.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's active character.</param>
    public static void OpenItems(GameSession session, Character character)
    {
        OpenServices.Open(session, character, ServiceWindow.BankItems);
        SendItems(session, character);
        session.Send(BankMessages.ShowItems());
    }

    /// <summary>Replaces the client's bank item list with the stored one: clears it, lists the stacks by slot and sends the gold balance.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's active character.</param>
    public static void SendItems(GameSession session, Character character)
    {
        session.Send(BankMessages.Clear());
        if (character.Bank.Count > 0)
            session.Send(BankMessages.List(character.Bank.OrderBy(b => b.Slot).Select(b => ToEntry(b))));
        session.Send(BankMessages.Balance(character.BankGold));
    }

    /// <summary>Opens the bank's money window with the current bank balance and records it as open on the player's map.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's active character.</param>
    public static void OpenMoney(GameSession session, Character character)
    {
        OpenServices.Open(session, character, ServiceWindow.BankMoney);
        session.Send(BankMessages.Balance(character.BankGold));
        session.Send(BankMessages.ShowMoney());
    }

    /// <summary>
    /// Moves whole bag stacks into the bank (<c>30/2</c>). A slot with nothing, with a stack committed elsewhere (<see cref="BagLocks"/>) or with a
    /// no-store item is skipped; a stack that does not fit ends the order with the bank-full notice.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: one byte per bag slot.</param>
    internal ValueTask OnStore(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (CharacterOf(session, "store", ServiceWindow.BankItems) is not { } character)
            return ValueTask.CompletedTask;
        var moved = new List<string>();
        foreach (var slot in body.ToArray().Distinct())
        {
            var stack = character.Bag.Find(b => b.Slot == slot);
            var locked = BagLocks.LockedBy(session, slot);
            if (stack is null || locked is not null || NoStore(stack))
            {
                logger.LogWarning("[{Session}] bank store: bag slot {Slot} cannot be stored ({Locked})", session.Id, slot, locked ?? "empty or no-store");
                continue;
            }
            if (!AllFit(character.Bank, stack.ItemId, stack.Count))
            {
                session.Send(BankMessages.Full());
                break;
            }
            foreach (var (bankStack, added) in Bag.Add(
                character.Bank,
                items,
                stack.ItemId,
                stack.Count,
                stack.Wear,
                stack.Insert,
                stack.Fusion,
                stack.Pose,
                stack.BoxItem,
                stack.Used,
                stack.Extra))
                session.Send(BankMessages.Put(ToEntry(bankStack, added)));
            session.Send(ItemMessages.BagSlotDecrease(stack.Slot, stack.Count));
            character.Bag.Remove(stack);
            moved.Add($"{items[stack.ItemId]?.Name} x{stack.Count}");
        }
        session.Send(BankMessages.StoreDone());
        logger.LogInformation("[{Session}] {Name} stores {Items}", session.Id, character.Name, moved.Count > 0 ? string.Join(", ", moved) : "nothing");
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Moves whole bank stacks into the bag (<c>30/1</c>). An empty slot is skipped; a stack that does not fit ends the order with the bag-full
    /// notice.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: one byte per bank slot.</param>
    internal ValueTask OnWithdraw(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (CharacterOf(session, "withdraw", ServiceWindow.BankItems) is not { } character)
            return ValueTask.CompletedTask;
        var moved = new List<string>();
        foreach (var slot in body.ToArray().Distinct())
        {
            var stack = character.Bank.Find(b => b.Slot == slot);
            if (stack is null)
            {
                logger.LogWarning("[{Session}] bank withdraw: bank slot {Slot} is empty", session.Id, slot);
                continue;
            }
            if (!AllFit(character.Bag, stack.ItemId, stack.Count))
            {
                session.Send(ShopMessages.BuyResult(BuyResult.InventoryFull));
                break;
            }
            Bag.SendAdded(
                session,
                Bag.Add(
                    character.Bag,
                    items,
                    stack.ItemId,
                    stack.Count,
                    stack.Wear,
                    stack.Insert,
                    stack.Fusion,
                    stack.Pose,
                    stack.BoxItem,
                    stack.Used,
                    stack.Extra));
            session.Send(BankMessages.SlotDecrease(stack.Slot, stack.Count));
            character.Bank.Remove(stack);
            moved.Add($"{items[stack.ItemId]?.Name} x{stack.Count}");
        }
        session.Send(BankMessages.WithdrawDone());
        logger.LogInformation("[{Session}] {Name} withdraws {Items}", session.Id, character.Name, moved.Count > 0 ? string.Join(", ", moved) : "nothing");
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Moves a bank stack inside the bank: merges it into the same stackable item, moves it to a free place, or swaps two 1 x 1 items.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: u8 source bank slot, u8 destination bank slot.</param>
    internal ValueTask OnMove(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (CharacterOf(session, "move", ServiceWindow.BankItems) is not { } character || body.Length < 2)
            return ValueTask.CompletedTask;
        var (srcSlot, dstSlot) = (body.Span[0], body.Span[1]);
        var bank = character.Bank;
        if (Bag.Covering(bank, items, srcSlot) is not { } src || dstSlot is < BagItem.FirstSlot or > BagItem.LastSlot)
        {
            logger.LogWarning("[{Session}] bank move {Src} -> {Dst}: nothing at the source or bad target", session.Id, srcSlot, dstSlot);
            return ValueTask.CompletedTask;
        }
        var dst = Bag.Covering(bank, items, dstSlot);
        var record = items[src.ItemId];
        if (dst is not null && !ReferenceEquals(dst, src) && dst.ItemId == src.ItemId && Bag.IsStackable(record))
        {
            var moved = (byte)Math.Min(src.Count, Bag.MaxStack - dst.Count);
            if (moved == 0)
                return ValueTask.CompletedTask;
            dst.Count += moved;
            src.Count -= moved;
            session.Send(BankMessages.SlotDecrease(src.Slot, moved));
            session.Send(BankMessages.PutAtSlot(ToEntry(dst, moved)));
            if (src.Count == 0)
                bank.Remove(src);
            logger.LogInformation(
                "[{Session}] {Name} merges bank slot {Src} into {Dst}: {Item} x{Moved}",
                session.Id,
                character.Name,
                src.Slot,
                dst.Slot,
                record?.Name,
                moved);
            return ValueTask.CompletedTask;
        }
        if (dst is null || ReferenceEquals(dst, src))
        {
            if (src.Slot == dstSlot || !Bag.Fits(bank, items, src.ItemId, (byte)dstSlot, src))
            {
                logger.LogInformation("[{Session}] bank move {Src} -> {Dst}: does not fit", session.Id, srcSlot, dstSlot);
                return ValueTask.CompletedTask;
            }
            var old = src.Slot;
            session.Send(BankMessages.SlotDecrease(old, src.Count));
            src.Slot = (byte)dstSlot;
            session.Send(BankMessages.PutAtSlot(ToEntry(src)));
            logger.LogInformation(
                "[{Session}] {Name} moves bank slot {Old} to {New}: {Item} x{Count}",
                session.Id,
                character.Name,
                old,
                src.Slot,
                record?.Name,
                src.Count);
            return ValueTask.CompletedTask;
        }
        var other = items[dst.ItemId];
        if (record is not { CellWidth: <= 1, CellHeight: <= 1 } || other is not { CellWidth: <= 1, CellHeight: <= 1 })
        {
            logger.LogInformation("[{Session}] bank swap {Src} <-> {Dst}: only 1 x 1 items swap", session.Id, srcSlot, dstSlot);
            return ValueTask.CompletedTask;
        }
        var (a, b) = (src.Slot, dst.Slot);
        session.Send(BankMessages.SlotDecrease(a, src.Count));
        session.Send(BankMessages.SlotDecrease(b, dst.Count));
        (src.Slot, dst.Slot) = (b, a);
        session.Send(BankMessages.PutAtSlot(ToEntry(src)));
        session.Send(BankMessages.PutAtSlot(ToEntry(dst)));
        logger.LogInformation("[{Session}] {Name} swaps bank slots {A} and {B}: {X} / {Y}", session.Id, character.Name, a, b, record.Name, other.Name);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Drops a bag stack on a bank slot: onto the same stackable item there, or as a new stack where it fits. A stack committed elsewhere
    /// (<see cref="BagLocks"/>) and no-store items stay in the bag.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: u8 bank slot, u8 bag slot.</param>
    internal ValueTask OnDropIn(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (CharacterOf(session, "drop in", ServiceWindow.BankItems) is not { } character || body.Length < 2)
            return ValueTask.CompletedTask;
        var (bankSlot, bagSlot) = (body.Span[0], body.Span[1]);
        var stack = Bag.Covering(character.Bag, items, bagSlot);
        var locked = stack is null ? null : BagLocks.LockedBy(session, stack.Slot);
        if (stack is null || locked is not null || NoStore(stack) || bankSlot is < BagItem.FirstSlot or > BagItem.LastSlot)
        {
            logger.LogWarning(
                "[{Session}] bank drop in: bag slot {Bag} cannot be stored ({Locked}) or bad bank slot {Bank}",
                session.Id,
                bagSlot,
                locked ?? "empty or no-store",
                bankSlot);
            return ValueTask.CompletedTask;
        }
        var target = Bag.Covering(character.Bank, items, bankSlot);
        BankEntry entry;
        byte moved;
        if (target is not null)
        {
            if (target.ItemId != stack.ItemId || !Bag.IsStackable(items[stack.ItemId]) || target.Count >= Bag.MaxStack)
            {
                logger.LogInformation("[{Session}] bank drop in: bank slot {Bank} is taken", session.Id, bankSlot);
                return ValueTask.CompletedTask;
            }
            moved = (byte)Math.Min(stack.Count, Bag.MaxStack - target.Count);
            target.Count += moved;
            entry = ToEntry(target, moved);
        }
        else
        {
            if (!Bag.Fits(character.Bank, items, stack.ItemId, bankSlot))
            {
                logger.LogInformation("[{Session}] bank drop in: {Item} does not fit at bank slot {Bank}", session.Id, items[stack.ItemId]?.Name, bankSlot);
                return ValueTask.CompletedTask;
            }
            moved = stack.Count;
            var stored = new BagItem
            {
                Slot = bankSlot,
                ItemId = stack.ItemId,
                Count = moved,
                Wear = stack.Wear,
                Insert = stack.Insert,
                Fusion = stack.Fusion,
                Pose = stack.Pose,
                BoxItem = stack.BoxItem,
                Used = stack.Used,
                Extra = stack.Extra
            };
            character.Bank.Add(stored);
            entry = ToEntry(stored);
        }
        session.Send(BankMessages.PutAtSlot(entry));
        session.Send(ItemMessages.BagSlotDecrease(stack.Slot, moved));
        stack.Count -= moved;
        if (stack.Count == 0)
            character.Bag.Remove(stack);
        logger.LogInformation(
            "[{Session}] {Name} drops bag slot {Bag} on bank slot {Bank}: {Item} x{Count}",
            session.Id,
            character.Name,
            bagSlot,
            entry.Slot,
            items[entry.ItemId]?.Name,
            moved);
        return ValueTask.CompletedTask;
    }

    /// <summary>Drags a bank stack out onto a bag slot: onto the same stackable item there, or as a new stack where it fits.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: u8 bag slot, u8 bank slot.</param>
    internal ValueTask OnDragOut(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (CharacterOf(session, "drag out", ServiceWindow.BankItems) is not { } character || body.Length < 2)
            return ValueTask.CompletedTask;
        var (bagSlot, bankSlot) = (body.Span[0], body.Span[1]);
        var stack = Bag.Covering(character.Bank, items, bankSlot);
        if (stack is null || bagSlot is < BagItem.FirstSlot or > BagItem.LastSlot)
        {
            logger.LogWarning("[{Session}] bank drag out: bank slot {Bank} is empty or bad bag slot {Bag}", session.Id, bankSlot, bagSlot);
            return ValueTask.CompletedTask;
        }
        var target = Bag.Covering(character.Bag, items, bagSlot);
        byte moved;
        BagItem into;
        if (target is not null)
        {
            if (target.ItemId != stack.ItemId || !Bag.IsStackable(items[stack.ItemId]) || target.Count >= Bag.MaxStack)
            {
                logger.LogInformation("[{Session}] bank drag out: bag slot {Bag} is taken", session.Id, bagSlot);
                return ValueTask.CompletedTask;
            }
            moved = (byte)Math.Min(stack.Count, Bag.MaxStack - target.Count);
            target.Count += moved;
            into = target;
        }
        else
        {
            if (!Bag.Fits(character.Bag, items, stack.ItemId, bagSlot))
            {
                logger.LogInformation("[{Session}] bank drag out: {Item} does not fit at bag slot {Bag}", session.Id, items[stack.ItemId]?.Name, bagSlot);
                return ValueTask.CompletedTask;
            }
            moved = stack.Count;
            into = new BagItem
            {
                Slot = bagSlot,
                ItemId = stack.ItemId,
                Count = moved,
                Wear = stack.Wear,
                Insert = stack.Insert,
                Fusion = stack.Fusion,
                Pose = stack.Pose,
                BoxItem = stack.BoxItem,
                Used = stack.Used,
                Extra = stack.Extra
            };
            character.Bag.Add(into);
        }
        session.Send(
            ItemMessages.BagAdd(
                new BagEntry
                {
                    Slot = into.Slot,
                    ItemId = into.ItemId,
                    Count = moved,
                    Wear = into.Wear,
                    Insert = into.Insert,
                    FusionLevel = into.Fusion,
                    PoseItemId = into.Pose,
                    BoxItemId = into.BoxItem,
                    UsedAmount = into.Used,
                    ExtraAmount = into.Extra
                }));
        session.Send(BankMessages.SlotDecrease(stack.Slot, moved));
        stack.Count -= moved;
        if (stack.Count == 0)
            character.Bank.Remove(stack);
        logger.LogInformation(
            "[{Session}] {Name} drags bank slot {Bank} to bag slot {Bag}: {Item} x{Count}",
            session.Id,
            character.Name,
            bankSlot,
            into.Slot,
            items[into.ItemId]?.Name,
            moved);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Moves carried gold into the bank balance (<c>29/2</c>). Refused when the amount is not positive, more than they carry, or would pass the
    /// limit; either way the pending-money state of the window is cleared.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: i32 gold amount.</param>
    internal ValueTask OnDeposit(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (CharacterOf(session, "gold deposit", ServiceWindow.BankMoney) is not { } character || body.Length < 4)
            return ValueTask.CompletedTask;
        var amount = new PacketReader(body.Span).I32();
        if (amount > 0 && amount <= character.Gold && character.BankGold + (long)amount <= MaxGold)
        {
            character.Gold -= amount;
            character.BankGold += amount;
            session.Send(BankMessages.Deposited(amount));
            session.Send(PlayerMessages.MoneyChange(-amount));
            logger.LogInformation(
                "[{Session}] {Name} deposits {Amount} gold (bank {Bank}, carried {Gold})",
                session.Id,
                character.Name,
                amount,
                character.BankGold,
                character.Gold);
        }
        else
        {
            logger.LogWarning(
                "[{Session}] {Name} cannot deposit {Amount} gold (carried {Gold}, bank {Bank})",
                session.Id,
                character.Name,
                amount,
                character.Gold,
                character.BankGold);
        }
        foreach (var packet in BankMessages.ClearMoneyPending())
            session.Send(packet);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Moves gold from the bank balance to the purse (<c>29/1</c>). Refused when the amount is not positive, more than the balance, or would pass the
    /// purse limit; either way the pending-money state of the window is cleared.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: i32 gold amount.</param>
    internal ValueTask OnWithdrawGold(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (CharacterOf(session, "gold withdraw", ServiceWindow.BankMoney) is not { } character || body.Length < 4)
            return ValueTask.CompletedTask;
        var amount = new PacketReader(body.Span).I32();
        if (amount > 0 && amount <= character.BankGold && character.Gold + (long)amount <= MaxGold)
        {
            character.BankGold -= amount;
            character.Gold += amount;
            session.Send(BankMessages.Withdrawn(amount));
            session.Send(PlayerMessages.MoneyChange(amount));
            logger.LogInformation(
                "[{Session}] {Name} withdraws {Amount} gold (bank {Bank}, carried {Gold})",
                session.Id,
                character.Name,
                amount,
                character.BankGold,
                character.Gold);
        }
        else
        {
            logger.LogWarning(
                "[{Session}] {Name} cannot withdraw {Amount} gold (bank {Bank}, carried {Gold})",
                session.Id,
                character.Name,
                amount,
                character.BankGold,
                character.Gold);
        }
        foreach (var packet in BankMessages.ClearMoneyPending())
            session.Send(packet);
        return ValueTask.CompletedTask;
    }

    /// <summary>Checks on a copy of the grid whether the whole count of an item would fit, without changing the grid.</summary>
    /// <param name="grid">The stacks of the bag or bank to try.</param>
    /// <param name="itemId">The item to add.</param>
    /// <param name="count">How many to add.</param>
    /// <returns>True when all of the items fit.</returns>
    private bool AllFit(List<BagItem> grid, ushort itemId, int count) => Bag.Add(Bag.Trial(grid), items, itemId, count).Sum(c => c.Added) == count;

    /// <summary>Whether a stack's item has the no-store flag (32), which keeps it out of the bank as out of the warehouse and the Item Bag.</summary>
    /// <param name="stack">The bag stack.</param>
    /// <returns>True when the stack must stay in the bag.</returns>
    private bool NoStore(BagItem stack) => ((items[stack.ItemId]?.Flags ?? 0) & MiniGridHandlers.NoStoreFlag) != 0;

    /// <summary>Converts a stored stack to the entry of a bank packet.</summary>
    /// <param name="b">The stored stack.</param>
    /// <param name="count">The count to send, for the part of a stack that moved; null for the whole stack.</param>
    /// <returns>The packet entry.</returns>
    private static BankEntry ToEntry(BagItem b, byte? count = null) =>
        new(b.Slot, b.ItemId, count ?? b.Count, b.Wear, b.Insert, b.Fusion, b.Pose, b.BoxItem, b.Used, b.Extra);

    /// <summary>
    /// Returns the active character when the session is in the world with a bank window of the kind open (<see cref="OpenServices"/>): one an
    /// NPC event or a Wit Maid opened on the player's map, out of battle. A refusal is logged.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="request">The name of the request, for the log.</param>
    /// <param name="window">The bank window the request belongs to.</param>
    /// <returns>The active character, or null when the request must be ignored.</returns>
    private Character? CharacterOf(GameSession session, string request, ServiceWindow window)
    {
        var character = session.ActiveCharacter;
        if (session.State != SessionState.InWorld || character is null)
            return null;
        if (OpenServices.IsOpen(session, character, window))
            return character;
        logger.LogWarning(
            "[{Session}] {Name}: bank {Request} refused, no {Window} open (map {Map}, window map {WindowMap}, in battle {Battle})",
            session.Id,
            character.Name,
            request,
            window,
            character.MapId,
            OpenServices.MapOf(session, window),
            session.Battle is not null);
        return null;
    }
}
