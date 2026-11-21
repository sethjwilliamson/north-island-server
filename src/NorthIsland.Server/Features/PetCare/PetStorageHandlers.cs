using Microsoft.Extensions.Logging;
using NorthIsland.Protocol.IO;
using NorthIsland.Protocol.Messages.Player;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.Features.PetCare;

/// <summary>Handles the pet storage of the inn: showing it and moving pets between the team and the storage.</summary>
/// <param name="world">The world state, passed on when a pet leaves the player.</param>
/// <param name="space">The space contract, which tells how many places the storage has.</param>
/// <param name="combat">The combat stats, resent when the player gets off a pet that goes into the storage.</param>
/// <param name="npcs">The npc data, which tells the soldiers (mercenaries) apart; they cannot be stored.</param>
/// <param name="logger">The logger.</param>
public sealed class PetStorageHandlers(
    WorldState world,
    SpaceContract space,
    CombatStats combat,
    NpcData npcs,
    ILogger<PetStorageHandlers> logger)
{
    /// <summary>
    /// Opens the storage for a player from an NPC's event: records it as open on the player's map (<see cref="OpenServices"/>), sends its state
    /// and list, then the open message.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's character.</param>
    public void Open(GameSession session, Character character)
    {
        OpenServices.Open(session, character, ServiceWindow.PetStorage);
        SendState(session, character);
        session.Send(InnMessages.StorageOpen());
        logger.LogInformation("[{Session}] pet storage ({Count} stored)", session.Id, character.StoredPets.Count);
    }

    /// <summary>Sends the storage state (whether the player has the hotel contract) and the list of stored pets.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's character.</param>
    public void SendState(GameSession session, Character character)
    {
        session.Send(new PacketWriter(Action23.Code, Action23.SpaceContract).U8(space.Has(character, SpaceContract.Hotel) ? (byte)3 : (byte)4).ToArray());
        SendList(session, character);
    }

    /// <summary>Sends the list of stored pets when there are any.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's character.</param>
    public static void SendList(GameSession session, Character character)
    {
        if (character.StoredPets.Count > 0)
            session.Send(InnMessages.StorageList(character.StoredPets.Select(p => (Pets.Info(p), WornBySlot(p))), session.Client));
    }

    /// <summary>Lists what a pet wears in equipment slots 1..6.</summary>
    /// <param name="p">The pet.</param>
    /// <returns>The item id in each slot; 0 for an empty slot.</returns>
    private static ushort[] WornBySlot(Pet p) =>
        [.. Enumerable.Range(1, 6).Select(slot => p.Equipment.Find(e => e.Slot == slot)?.ItemId ?? (ushort)0)];

    /// <summary>Reads the swap packet and moves the pets.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the count and the team slots to store, then the count and the storage places to take.</param>
    internal ValueTask OnSwap(GameSession session, ReadOnlyMemory<byte> body)
    {
        var span = body.Span;
        if (span.Length < 1 || span.Length < 2 + span[0] || span.Length < 2 + span[0] + span[1 + span[0]])
            return ValueTask.CompletedTask;
        var n = span[0];
        var m = span[1 + n];
        return OnMove(session, span.Slice(1, n).ToArray(), span.Slice(2 + n, m).ToArray());
    }

    /// <summary>
    /// Moves pets between the team and the storage in one step; the whole move is refused when the storage is not open on the player's map (an
    /// NPC or a Wit Maid opened it there, <see cref="OpenServices"/>) or a pet trade is open, or when any part breaks a rule (an empty place, a
    /// ridden or stalled pet, a pet that cannot be stored or a soldier (mercenary), no room, or two pets of one kind on a side).
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="storeSlots">The team slots of the pets to store.</param>
    /// <param name="withdrawIdx">The storage places of the pets to take back.</param>
    internal ValueTask OnMove(GameSession session, byte[] storeSlots, byte[] withdrawIdx)
    {
        var character = session.ActiveCharacter;
        if (session.State != SessionState.InWorld || character is null || session.Battle is not null)
            return ValueTask.CompletedTask;
        var toStore = storeSlots.Distinct().Select(s => character.Pets.Find(p => p.Slot == s)).ToList();
        var toTake = withdrawIdx.Distinct().Select(i => character.StoredPets.Find(p => p.Slot == i)).ToList();

        string? refusal = !OpenServices.IsOpen(session, character, ServiceWindow.PetStorage) ? "no pet storage open here"
            : session.Trades.Pets is not null ? "a pet trade is open"
                : toStore.Exists(p => p is null) || toTake.Exists(p => p is null) ? "an empty place"
                    : toStore.Exists(p => p!.Slot == session.RidingPetSlot) ? "rides that pet"
                        : toStore.Exists(p => session.Stalls.Pets?.Prices.ContainsKey(p!.Slot) == true) ? "that pet is in his stall"
                            : toStore.Exists(p => p!.Tradable != 1) ? "that pet cannot be stored"
                                : toStore.Exists(p => npcs[p!.NpcId] is { IsMercenary: true }) ? "soldiers cannot be stored"
                                    : character.Pets.Count - toStore.Count + toTake.Count > Pet.MaxPets ? "too many pets"
                                        : toStore.Count > toTake.Count
                                          && character.StoredPets.Count - toTake.Count + toStore.Count > space.HotelSlots(character)
                                            ? "the storage is full"
                                            : null;
        var carried = character.Pets.Except(toStore).Concat(toTake).Select(p => p!.NpcId).ToList();
        var stored = character.StoredPets.Except(toTake).Concat(toStore).Select(p => p!.NpcId).ToList();
        if (refusal is null && carried.Distinct().Count() != carried.Count)
        {
            refusal = "same kind carried";
            session.Send(InnMessages.StorageRefused(storerHasIt: false));
        }
        else if (refusal is null && stored.Distinct().Count() != stored.Count)
        {
            refusal = "same kind stored";
            session.Send(InnMessages.StorageRefused(storerHasIt: true));
        }
        if (refusal is not null)
        {
            logger.LogWarning("[{Session}] {Name}: pet storage refused ({Why})", session.Id, character.Name, refusal);
            session.Send(InnMessages.StorageDone());
            return ValueTask.CompletedTask;
        }

        foreach (var pet in toTake)
            character.StoredPets.Remove(pet!);
        var takenIdx = toTake.Select(p => p!.Slot).ToList();
        var free = new Queue<byte>(Enumerable.Range(1, InnMessages.MaxStoredPets).Select(i => (byte)i)
            .Where(i => !character.StoredPets.Exists(p => p.Slot == i)));
        var storedAt = new List<(Pet Pet, byte Idx)>();
        foreach (var pet in toStore)
        {
            var idx = free.Dequeue();
            session.Send(InnMessages.Stored(idx, pet!.Slot));
            storedAt.Add((pet, idx));
        }
        foreach (var (pet, idx) in storedAt)
        {
            Pets.Exile(session, character, pet, world, combat);
            pet.Slot = idx;
            character.StoredPets.Add(pet);
        }
        foreach (var pet in toTake)
        {
            var idx = pet!.Slot;
            PetTradeHandlers.Receive(session, character, pet);
            session.Send(InnMessages.Retrieved(idx, pet.Slot));
            if (!storedAt.Exists(s => s.Idx == idx))
                session.Send(InnMessages.StorageCleared(idx));
        }
        session.Send(InnMessages.StorageDone());
        logger.LogInformation("[{Session}] {Name} stores {Stored} and takes {Taken} (places {Places})", session.Id, character.Name,
            string.Join(", ", storedAt.Select(s => $"{s.Pet.Name} -> {s.Idx}")), string.Join(", ", toTake.Select(p => $"{p!.Name} -> slot {p.Slot}")),
            string.Join(",", takenIdx));
        return ValueTask.CompletedTask;
    }
}
