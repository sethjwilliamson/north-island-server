using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using NorthIsland.Protocol.IO;
using NorthIsland.Protocol.Messages.Items;
using NorthIsland.Protocol.Messages.Player;

namespace NorthIsland.Server.Features.Stalls;

/// <summary>Handles pet stalls: opening, closing, browsing and buying.</summary>
/// <param name="world">World state.</param>
/// <param name="npcs">NPC data, for the kind of each pet.</param>
/// <param name="items">Item data, for the gear the pets wear.</param>
/// <param name="combat">Combat stats, resent when the seller gets off a pet they sell.</param>
/// <param name="logger">Logger.</param>
public sealed class PetStallHandlers(
    WorldState world,
    NpcData npcs,
    ItemData items,
    CombatStats combat,
    ILogger<PetStallHandlers> logger)
{
    /// <summary>Most pets one stall can offer.</summary>
    public const int MaxPets = 3;

    /// <summary>Tells why a pet of the seller cannot be sold, by <see cref="PetTransfer.Refusal"/>.</summary>
    /// <param name="session">The seller's session.</param>
    /// <param name="seller">The seller's character.</param>
    /// <param name="pet">The pet.</param>
    /// <returns>The reason, for the log, or null when the pet can be sold.</returns>
    private string? Unsellable(GameSession session, Character seller, Pet pet) =>
        PetTransfer.Refusal(session, seller, pet, PetTransferVia.Stall, npcs, items)?.Why;

    /// <summary>Closes the player's pet stall and tells them and the map; nothing happens when none is open.</summary>
    /// <param name="session">The owner's session.</param>
    /// <param name="world">World state, for the broadcast.</param>
    public static void Close(GameSession session, WorldState world)
    {
        if (session.Stalls.Pets is null || session.Account is null)
            return;
        session.Stalls.Pets = null;
        var packet = StallMessages.PetStallClosed(session.Account.LoginId);
        session.Send(packet);
        world.Broadcast(session, packet, sameMapOnly: true);
    }

    /// <summary>Opens a pet stall from the player's list of priced pets.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: name, optional sign byte, then pairs of pet slot and price.</param>
    internal ValueTask OnOpen(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InWorld(session) is not { } character || session.Stalls.Any || session.Battle is not null || body.Length < 2)
            return ValueTask.CompletedTask;
        var r = new PacketReader(body.Span);
        var stall = new PetStall { Name = r.PStr8(), Sign = r.Remaining > 0 ? r.U8() : (byte)0 };
        while (r.Remaining >= 5 && stall.Prices.Count < MaxPets)
        {
            var (slot, price) = (r.U8(), r.I32());
            if (price <= 0 || character.Pets.Find(p => p.Slot == slot) is not { } pet)
                continue;
            if (Unsellable(session, character, pet) is { } why)
            {
                logger.LogWarning("[{Session}] {Name}: pet {Pet} left out of the pet stall: {Why}", session.Id, character.Name, pet.Name, why);
                continue;
            }
            stall.Prices[slot] = price;
            stall.Offered[slot] = pet;
        }
        if (stall.Prices.Count == 0 || stall.Name.Length == 0)
        {
            logger.LogWarning("[{Session}] {Name}: pet stall refused (no priced pets or no name)", session.Id, character.Name);
            return ValueTask.CompletedTask;
        }
        session.Stalls.Pets = stall;
        session.Send(StallMessages.PetStallOpened(stall.Name, stall.Sign, stall.Prices.Select(p => (p.Key, p.Value))));
        world.Broadcast(session, StallMessages.PetStallList([(session.Account!.LoginId, stall.Name, stall.Sign)]), sameMapOnly: true);
        logger.LogInformation("[{Session}] {Name} opens pet stall '{Stall}': {Pets}", session.Id, character.Name, stall.Name,
            string.Join(", ", stall.Prices.Select(p => $"{character.Pets.Find(x => x.Slot == p.Key)?.Name} at {p.Value}")));
        return ValueTask.CompletedTask;
    }

    /// <summary>Closes the player's pet stall.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body; not read.</param>
    internal ValueTask OnClose(GameSession session, ReadOnlyMemory<byte> body)
    {
        Close(session, world);
        return ValueTask.CompletedTask;
    }

    /// <summary>Sends the pets on offer in a stall, or a closed notice when there is none.</summary>
    /// <param name="session">The browsing player's session.</param>
    /// <param name="body">The packet body: the owner's login id.</param>
    internal ValueTask OnBrowse(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InWorld(session) is null || body.Length < 4)
            return ValueTask.CompletedTask;
        var ownerId = BinaryPrimitives.ReadInt32LittleEndian(body.Span);
        if (world.SessionOf(ownerId) is not { } owner || owner.Stalls.Pets is not { } stall || owner.ActiveCharacter is not { } seller
            || seller.MapId != session.ActiveCharacter!.MapId)
        {
            session.Send(StallMessages.PetStallClosed(ownerId));
            return ValueTask.CompletedTask;
        }
        lock (stall)
            Show(session, ownerId, stall, owner, seller);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Buys pets from a stall on the buyer's map: under the seller's session gate, checks each pet is still the one the stall opened with, can
    /// still be sold and is at the price the buyer was shown, then gold, room and duplicate kinds, then moves the pets and the gold.
    /// </summary>
    /// <param name="session">The buyer's session.</param>
    /// <param name="body">The packet body: the owner's login id, then the pet slots.</param>
    internal ValueTask OnBuy(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InWorld(session) is not { } buyer || body.Length < 5)
            return ValueTask.CompletedTask;
        var ownerId = BinaryPrimitives.ReadInt32LittleEndian(body.Span);
        var slots = body.Span[4..].ToArray().Distinct().ToList();
        if (world.SessionOf(ownerId) is not { } owner || ReferenceEquals(owner, session)
            || owner.Stalls.Pets is not { } stall || owner.ActiveCharacter is not { } seller)
            return ValueTask.CompletedTask;
        if (seller.MapId != buyer.MapId)
        {
            logger.LogWarning("[{Session}] {Buyer} cannot buy from {Seller}'s pet stall: on another map", session.Id, buyer.Name, seller.Name);
            session.Send(StallMessages.PetStallClosed(ownerId));
            return ValueTask.CompletedTask;
        }

        if (!owner.TryExclusiveFrom(session, out var held)) // the seller's own packet may be trading, releasing or moving the same pets
        {
            logger.LogWarning("[{Session}] {Buyer} cannot buy from {Seller}'s pet stall: the seller's session is busy", session.Id, buyer.Name,
                seller.Name);
            return ValueTask.CompletedTask;
        }
        using (held)
        {
            if (!ReferenceEquals(owner.Stalls.Pets, stall))
            {
                session.Send(StallMessages.PetStallClosed(ownerId));
                return ValueTask.CompletedTask;
            }
            lock (stall)
            {
                List<(Pet Pet, int Price)> lines = [];
                foreach (var slot in slots)
                {
                    var priced = stall.Prices.TryGetValue(slot, out var price);
                    var pet = seller.Pets.Find(p => p.Slot == slot);
                    var why = !priced || !stall.Offered.TryGetValue(slot, out var offered) ? "not on offer"
                        : pet is null || !ReferenceEquals(pet, offered) ? "no longer the pet the stall opened with"
                            : owner.Battle is not null ? "the seller is in a battle"
                                : Unsellable(owner, seller, pet) is { } unsellable ? unsellable
                                    : !StallView.Saw(session.Stalls.SeenPets, stall, slot, price) ? "not the price the buyer was shown"
                                        : null;
                    if (why is not null)
                    {
                        logger.LogWarning("[{Session}] pet stall buy: slot {Slot} refused: {Why}", session.Id, slot, why);
                        Show(session, ownerId, stall, owner, seller);
                        return ValueTask.CompletedTask;
                    }
                    lines.Add((pet!, price));
                }
                var total = lines.Sum(l => (long)l.Price);
                string? refusal = total > buyer.Gold ? "not enough gold"
                    : (long)seller.Gold + total > BankHandlers.MaxGold ? "the seller's purse is full"
                        : buyer.Pets.Count + lines.Count > Pet.MaxPets ? "too many pets"
                            : lines.Exists(l => buyer.Pets.Exists(p => p.NpcId == l.Pet.NpcId)) || lines.DistinctBy(l => l.Pet.NpcId).Count() != lines.Count
                                ? "has that kind of pet"
                                : null;
                if (refusal is not null)
                {
                    logger.LogInformation("[{Session}] {Buyer} cannot buy from {Seller}'s pet stall: {Why}", session.Id, buyer.Name, seller.Name, refusal);
                    return ValueTask.CompletedTask;
                }

                foreach (var (pet, price) in lines)
                {
                    stall.Prices.Remove(pet.Slot);
                    stall.Offered.Remove(pet.Slot);
                    owner.Send(StallMessages.PetSold(pet.NpcId, price));
                    Pets.Exile(owner, seller, pet, world, combat);
                    PetTradeHandlers.Receive(session, buyer, pet);
                    logger.LogInformation("[{Session}] {Buyer} buys pet {Pet} from {Seller}'s stall for {Gold}", session.Id, buyer.Name, pet.Name,
                        seller.Name, price);
                }
                buyer.Gold -= (int)total;
                session.Send(PlayerMessages.MoneyChange(-(int)total));
                seller.Gold += (int)total;
                owner.Send(PlayerMessages.MoneyChange((int)total));
                if (stall.Prices.Count == 0)
                    Close(owner, world);
                else
                    Show(session, ownerId, stall, owner, seller);
            }
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>Sends a buyer the pets of a stall that are still the ones it opened with and can still be sold, and remembers what they saw.</summary>
    /// <param name="session">The buyer's session.</param>
    /// <param name="ownerId">The owner's login id.</param>
    /// <param name="stall">The stall, locked by the caller.</param>
    /// <param name="owner">The owner's session.</param>
    /// <param name="seller">The owner's character.</param>
    private void Show(GameSession session, int ownerId, PetStall stall, GameSession owner, Character seller)
    {
        var shown = stall.Prices
            .Select(p => (Slot: p.Key, Price: p.Value, Pet: seller.Pets.Find(x => x.Slot == p.Key)))
            .Where(x => x.Pet is not null && stall.Offered.TryGetValue(x.Slot, out var offered) && ReferenceEquals(x.Pet, offered)
                && Unsellable(owner, seller, x.Pet) is null)
            .ToList();
        session.Stalls.SeenPets = new StallView(stall, shown.ToDictionary(x => x.Slot, x => x.Price));
        session.Send(StallMessages.PetStallOffer(ownerId, shown.Select(x => (x.Slot, x.Price, Pets.Info(x.Pet!), x.Pet!.MaxHp, x.Pet!.MaxSp)),
            session.Client));
    }

    /// <summary>Gives the active character of a session that is in the world.</summary>
    /// <param name="session">The player's session.</param>
    /// <returns>The character, or null when the session is not in the world.</returns>
    private static Character? InWorld(GameSession session)
    {
        var character = session.ActiveCharacter;
        return session.State == SessionState.InWorld && character is not null && session.Account is not null ? character : null;
    }
}
