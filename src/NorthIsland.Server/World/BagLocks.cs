namespace NorthIsland.Server.World;

/// <summary>
/// Tells whether a bag slot of a session's active character is committed elsewhere and must not change: offered in an open item trade, priced
/// in the player's item stall or utensil stall, listed on one of the player's house shelves or vending machines, loaded into the pet express
/// trip under way, or the vehicle the player has out. Cheap and side-effect free, so any handler that takes, moves or replaces a bag stack can
/// ask it first.
/// </summary>
public static class BagLocks
{
    /// <summary>Whether a bag slot is committed elsewhere.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="slot">The bag slot.</param>
    /// <returns>True when the stack in the slot must stay as it is.</returns>
    public static bool IsLocked(GameSession session, byte slot) => LockedBy(session, slot) is not null;

    /// <summary>Tells what a bag slot is committed to, for the log of a refusal.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="slot">The bag slot.</param>
    /// <param name="ignoreTrade">
    /// When true, the player's own offer in an open item trade does not count, so the trade can ask what else holds a slot it is offered in.
    /// </param>
    /// <returns>A short reason such as "offered in a trade", or null when the slot is free to change.</returns>
    public static string? LockedBy(GameSession session, byte slot, bool ignoreTrade = false)
    {
        if (!ignoreTrade && session.Trades.Items is { Closed: false } trade && trade.Of(session).Stacks.Exists(s => s.Slot == slot))
            return "offered in a trade";
        if (session.Stalls.Items?.Prices.ContainsKey(slot) == true)
            return "priced in the item stall";
        if (session.Stalls.Utensils?.Prices.ContainsKey(slot) == true)
            return "priced in the utensil stall";
        if (session.PetJobs.Express?.Stacks.Exists(s => s.Slot == slot) == true)
            return "loaded for the pet express";
        if (session.Vehicle?.BagSlot == slot)
            return "the vehicle in use";
        if (session.ActiveCharacter is { } character && character.Furniture.Exists(f => IsHomeShop(f) && f.Stock.Exists(s => s.BagCell == slot)))
            return "listed on a house shelf";
        return null;
    }

    /// <summary>Whether a piece of furniture is a house shop whose offers stay in the owner's bag until sold: a shelf or a vending machine.</summary>
    /// <param name="piece">The piece of furniture.</param>
    /// <returns>True for a shelf or a vending machine.</returns>
    private static bool IsHomeShop(Furniture piece) => piece.ItemId is HomeShopHandlers.Shelf or HomeShopHandlers.VendingMachine;
}
