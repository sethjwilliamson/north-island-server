namespace NorthIsland.Server.Features.PetCare;

/// <summary>How a pet goes to another player, so the rule does not count the hold that very way has on the pet.</summary>
public enum PetTransferVia
{
    /// <summary>Offered or given in a pet trade.</summary>
    Trade,

    /// <summary>Priced in or sold from the owner's pet stall.</summary>
    Stall,
}

/// <summary>The reasons a pet may not go to another player.</summary>
public enum PetTransferBlock
{
    /// <summary>The pet is no longer in the owner's team.</summary>
    NotInTeam,

    /// <summary>The pet is a grow kind or a reborn pet.</summary>
    Kind,

    /// <summary>The pet is a soldier (mercenary).</summary>
    Mercenary,

    /// <summary>The pet is marked as not tradable.</summary>
    Untradable,

    /// <summary>The pet has no amity.</summary>
    NoAmity,

    /// <summary>The owner rides the pet.</summary>
    Ridden,

    /// <summary>The pet is the owner's fighting pet.</summary>
    Fighting,

    /// <summary>The pet is priced in the owner's pet stall.</summary>
    InStall,

    /// <summary>The pet is offered in the owner's pet trade.</summary>
    InTrade,

    /// <summary>The pet wears gear that cannot be traded.</summary>
    NoTradeGear,
}

/// <summary>
/// The one rule for a pet going to another player, by the client's pet trade (<c>PetTradeWindow::petMayGo</c>) and pet stall: used by the pet
/// trade offer and its execution and by the pet stall's opening, listing and sales.
/// </summary>
public static class PetTransfer
{
    /// <summary>NPC type of a reborn pet, which the client never lets go to another player.</summary>
    public const byte RebornType = 32; // npc type 32: reborn pet

    /// <summary>
    /// Tells why a pet may not go to another player: it is no longer in the team, it is a grow kind or a reborn pet, it is a soldier (mercenary,
    /// which the client never lets go), it cannot be traded, it has no amity, it is ridden, it is the fighting pet, it is priced in the owner's pet
    /// stall or offered in the owner's pet trade (except for the way given), or it wears gear that cannot be traded.
    /// </summary>
    /// <param name="session">The owner's session.</param>
    /// <param name="owner">The owner's character.</param>
    /// <param name="pet">The pet.</param>
    /// <param name="via">How the pet goes, whose own hold on the pet is not counted.</param>
    /// <param name="npcs">The NPC data, for the pet's kind and whether it is a soldier.</param>
    /// <param name="items">The item data, for the flags of the gear the pet wears.</param>
    /// <returns>The reason and its text for the log, or null when the pet may go.</returns>
    public static (PetTransferBlock Block, string Why)? Refusal(
        GameSession session,
        Character owner,
        Pet pet,
        PetTransferVia via,
        NpcData npcs,
        ItemData items) =>
            !owner.Pets.Exists(p => ReferenceEquals(p, pet)) ? (PetTransferBlock.NotInTeam, "is no longer in the team")
                : npcs[pet.NpcId] is { } npc && (npc.GrowKey != 0 || npc.Type == RebornType) ? (PetTransferBlock.Kind, "is a grow or reborn kind")
                    : npcs[pet.NpcId] is { IsMercenary: true } ? (PetTransferBlock.Mercenary, "is a soldier (mercenary)")
                        : pet.Tradable == 0 ? (PetTransferBlock.Untradable, "cannot be traded")
                            : pet.Amity == 0 ? (PetTransferBlock.NoAmity, "has no amity")
                                : session.RidingPetSlot == pet.Slot ? (PetTransferBlock.Ridden, "is ridden")
                                    : owner.ActivePetId == pet.NpcId ? (PetTransferBlock.Fighting, "is the fighting pet")
                                        : via != PetTransferVia.Stall && session.Stalls.Pets?.Prices.ContainsKey(pet.Slot) == true
                                            ? (PetTransferBlock.InStall, "is in the pet stall")
                                            : via != PetTransferVia.Trade && session.Trades.Pets is { Closed: false } trade
                                                && ReferenceEquals(trade.Of(session).Pet, pet)
                                                ? (PetTransferBlock.InTrade, "is offered in a pet trade")
                                                : pet.Equipment.Exists(e => items[e.ItemId] is { } r && (r.Flags & Bag.NoTradeFlag) != 0)
                                                    ? (PetTransferBlock.NoTradeGear, "wears gear that cannot be traded")
                                                    : null;
}
