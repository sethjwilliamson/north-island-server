namespace NorthIsland.Server.Features.Stalls;

/// <summary>Checks that a bag stack offered in a stall is still the one the stall opened with.</summary>
public static class StallStock
{
    /// <summary>The item type of castle defence items (e.g. Gold Eagle Totem, Torch), which the item stall does not sell.</summary>
    public const byte CastleDefenceType = 53;

    /// <summary>
    /// Gives the stack in a priced bag slot when it is still the stack the stall opened with: the same item with the same attributes, no more of
    /// them than are left on offer, and not the vehicle the seller rides. A stack swapped into the slot is not offered.
    /// </summary>
    /// <param name="seller">The seller's character.</param>
    /// <param name="owner">The seller's session, for the ridden vehicle.</param>
    /// <param name="slot">The bag slot.</param>
    /// <param name="offered">The copy of the stack taken when the stall opened, its count lowered by each sale.</param>
    /// <returns>The stack, or null when the slot no longer holds it.</returns>
    public static BagItem? Find(Character seller, GameSession owner, byte slot, BagItem offered) =>
        seller.Bag.Find(b => b.Slot == slot) is { } stack && GridStore.Same(stack, offered) && stack.Count > 0 && stack.Count <= offered.Count
            && owner.Vehicle?.BagSlot != slot
            ? stack
            : null;

    /// <summary>
    /// Tells why a bag stack may not go to a buyer from a stall: its item cannot be traded or has a pose (<see cref="Bag.IsNoTrade"/>), or a magic
    /// crystal stone that cannot be passed on is set into it, as the client's item stall and the house shops refuse.
    /// </summary>
    /// <param name="items">The item data, for the flags.</param>
    /// <param name="stack">The bag stack.</param>
    /// <returns>The reason for the log, or null when the stack may be sold.</returns>
    public static string? Untransferable(ItemData items, BagItem stack) =>
        Bag.IsNoTrade(items, stack) ? "cannot be traded"
            : stack.Insert is >= HomeShopHandlers.FirstNonTransferable and <= HomeShopHandlers.LastNonTransferable
                ? "its crystal stone cannot be passed on"
                : null;

    /// <summary>
    /// Tells why a bag stack may not be sold in the item stall, by the client's rules (itemStallRefusal): it may not go to a buyer
    /// (<see cref="Untransferable"/>), its item is unknown, it is a vehicle or a castle defence item, or it is an accessory, instrument or piece
    /// of furniture larger than one cell (those go in the utensil stall).
    /// </summary>
    /// <param name="items">The item data, for the flags, types and sizes.</param>
    /// <param name="stack">The bag stack.</param>
    /// <returns>The reason for the log, or null when the stack may be sold in the item stall.</returns>
    public static string? ItemStallRefusal(ItemData items, BagItem stack) =>
        Untransferable(items, stack) is { } why ? why
            : items[stack.ItemId] is not { } r ? "unknown item"
                : r.Type == Bag.VehicleType ? "a vehicle cannot be sold in the item stall"
                    : r.Type == CastleDefenceType ? "a castle defence item cannot be sold in the item stall"
                        : r.Type is TentHandlers.AccessoryType or TentHandlers.InstrumentType or TentHandlers.FurnitureType
                            && (r.CellWidth != 1 || r.CellHeight != 1)
                            ? "a large item cannot be sold in the item stall"
                            : null;

    /// <summary>
    /// Tells why a bag slot may not be priced in a stall that is opening: the slot is empty, its stack may not go to a buyer
    /// (<see cref="Untransferable"/>), or the slot is committed elsewhere (<see cref="BagLocks.LockedBy"/>: offered in a trade, loaded for the pet
    /// express, listed on a house shelf, the vehicle in use).
    /// </summary>
    /// <param name="items">The item data, for the flags.</param>
    /// <param name="session">The owner's session, who has no stall open yet.</param>
    /// <param name="stack">The stack in the slot, or null when the slot is empty.</param>
    /// <param name="slot">The bag slot.</param>
    /// <returns>The reason for the log, or null when the slot may be priced.</returns>
    public static string? OpenRefusal(ItemData items, GameSession session, BagItem? stack, byte slot) =>
        stack is null ? "the slot is empty" : Untransferable(items, stack) ?? BagLocks.LockedBy(session, slot);

    /// <summary>Takes a copy of a stack to remember what a stall offers in a slot.</summary>
    /// <param name="stack">The bag stack.</param>
    /// <returns>The copy, not in any grid.</returns>
    public static BagItem Snapshot(BagItem stack) => GridStore.Copy(stack, stack.Slot, stack.Count);
}
