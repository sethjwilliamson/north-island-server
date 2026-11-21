using Microsoft.Extensions.Logging;
using NorthIsland.Protocol.IO;
using NorthIsland.Protocol.Messages.Player;
using NorthIsland.Protocol.Opcodes;
using NorthIsland.Protocol.Text;

namespace NorthIsland.Server.Features.Houses;

/// <summary>Pet beds in a player's house: puts a pet to sleep in a bed and takes it out again.</summary>
/// <param name="tents">The house and furniture handlers, for the house entry event.</param>
/// <param name="logger">The logger.</param>
public sealed class BedHandlers(
    TentHandlers tents,
    ILogger<BedHandlers> logger)
{
    /// <summary>The most pets that can sleep in beds at the same time, over all beds of a house.</summary>
    public const byte MaxSleepers = 6;

    /// <summary>Hooks the house entry event so the owner is shown the sleeping pets.</summary>
    internal void Start()
    {
        tents.HouseEntered += OnHouseEntered;
    }

    /// <summary>How many pets a bed piece holds.</summary>
    /// <param name="furnitureItem">The item id of the furniture piece.</param>
    /// <returns>1 for the Hammock, 2 for the Wooden Bed, 4 for the Bunk Bed, 0 for any other piece.</returns>
    public static int Capacity(ushort furnitureItem) => furnitureItem switch
    {
        39009 => 1, // Hammock
        39012 => 2, // Wooden Bed
        39124 => 4, // Bunk Bed
        _ => 0 // any other piece holds no pets
    };

    /// <summary>The active character of a session that is in the world and stands in their own house.</summary>
    /// <param name="session">The session.</param>
    /// <returns>The character, or null when the session is not in the world or not in its own house.</returns>
    private static Character? InOwnHouse(GameSession session)
    {
        var character = session.ActiveCharacter;
        return session.State == SessionState.InWorld && character is not null && session.Account is not null
            && TentHandlers.HouseMapOf(session.Account.LoginId) == character.MapId ? character : null;
    }

    /// <summary>How many pets are asleep in all beds of a character.</summary>
    /// <param name="c">The character.</param>
    /// <returns>The number of sleeping pets.</returns>
    private static int Used(Character c) => c.Furniture.Sum(f => f.Sleepers.Count);

    /// <summary>
    /// Handles <c>62/43</c>: the owner puts a pet into a bed; the pet leaves their pet list and sleeps there. Refused while a pet trade is
    /// open, so a pet on offer cannot leave the team before the trade is carried out.
    /// </summary>
    /// <param name="session">The owner's session.</param>
    /// <param name="body">The furniture index (two bytes) and the pet slot.</param>
    internal ValueTask OnStore(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InOwnHouse(session) is not { } c || body.Length < 3 || session.Battle is not null)
            return ValueTask.CompletedTask;
        var r = new PacketReader(body.Span);
        var (furn, petSlot) = (r.U16(), r.U8());
        var piece = c.Furniture.Find(f => f.Index == furn);
        var pet = c.Pets.Find(p => p.Slot == petSlot);
        var capacity = piece is null ? 0 : Capacity(piece.ItemId);
        if (piece is null || pet is null || capacity == 0 || piece.Sleepers.Count >= capacity || Used(c) >= MaxSleepers || pet.Tradable != 1
            || session.RidingPetSlot == petSlot || session.Stalls.Pets is not null || c.ActivePetId == pet.NpcId || session.Trades.Pets is not null)
        {
            logger.LogWarning("[{Session}] {Name}: pet slot {Slot} into bed {Furn} refused", session.Id, c.Name, petSlot, furn);
            return ValueTask.CompletedTask;
        }
        var bed = Enumerable.Range(1, capacity).Select(i => (byte)i).First(i => !piece.Sleepers.Exists(s => s.Bed == i));
        var id = Enumerable.Range(1, MaxSleepers).Select(i => (byte)i).First(i => !c.Furniture.Exists(f => f.Sleepers.Exists(s => s.Id == i)));
        piece.Sleepers.Add(new BedSleeper { Bed = bed, Id = id, Pet = pet });
        c.Pets.Remove(pet);
        session.Send(PetMessages.Removed(session.Account!.LoginId, petSlot));
        session.Send(Stored(furn, bed, id, pet));
        logger.LogInformation("[{Session}] {Name} puts {Pet} into bed {Furn}", session.Id, c.Name, pet.Name, furn);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Handles <c>62/44</c>: the owner takes a sleeping pet out of a bed into a free pet slot; the pet comes back with full HP and SP.
    /// </summary>
    /// <param name="session">The owner's session.</param>
    /// <param name="body">The furniture index (two bytes) and the bed number in that piece.</param>
    internal ValueTask OnTake(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InOwnHouse(session) is not { } c || body.Length < 3)
            return ValueTask.CompletedTask;
        var r = new PacketReader(body.Span);
        var (furn, bed) = (r.U16(), r.U8());
        var piece = c.Furniture.Find(f => f.Index == furn);
        if (piece?.Sleepers.Find(s => s.Bed == bed) is not { } sleeper)
            return ValueTask.CompletedTask;
        var slot = Enumerable.Range(1, Pet.MaxPets).Select(i => (byte)i).FirstOrDefault(i => !c.Pets.Exists(p => p.Slot == i));
        if (slot == 0)
            return ValueTask.CompletedTask;
        piece.Sleepers.Remove(sleeper);
        var pet = sleeper.Pet;
        pet.Slot = slot;
        c.Pets.Add(pet);
        session.Send(PetMessages.Joined(session.Account!.LoginId, Pets.Info(pet)));
        session.Send(new PacketWriter(Action62.Code, Action62.BedTake).U16(furn).U8(bed).U8(sleeper.Id).U8(slot).ToArray());
        if (pet.Hp < pet.MaxHp || pet.Sp < pet.MaxSp)
        {
            (pet.Hp, pet.Sp) = (pet.MaxHp, pet.MaxSp);
            session.Send(PetMessages.SetAttribute(slot, Action8.AttrHp, pet.Hp));
            session.Send(PetMessages.SetAttribute(slot, Action8.AttrSp, pet.Sp));
        }
        logger.LogInformation("[{Session}] {Name} takes {Pet} out of bed {Furn} into slot {Slot}", session.Id, c.Name, pet.Name, furn, slot);
        return ValueTask.CompletedTask;
    }

    /// <summary>Shows the owner the pets that sleep in their beds when they enter their house.</summary>
    /// <param name="session">The session that entered the house.</param>
    /// <param name="owner">The house owner's session, or null when the owner is not online.</param>
    private void OnHouseEntered(GameSession session, GameSession? owner)
    {
        if (owner != session || session.ActiveCharacter is not { } c)
            return;
        foreach (var piece in c.Furniture)
            foreach (var s in piece.Sleepers)
                session.Send(Stored(piece.Index, s.Bed, s.Id, s.Pet));
    }

    /// <summary>Builds the packet <c>62/43</c> that shows a pet asleep in a bed.</summary>
    /// <param name="furn">The furniture index of the bed piece.</param>
    /// <param name="bed">The bed number in that piece.</param>
    /// <param name="id">The sleeper id, unique in the house.</param>
    /// <param name="pet">The pet.</param>
    /// <returns>The packet.</returns>
    private static byte[] Stored(ushort furn, byte bed, byte id, Pet pet)
    {
        var w = new PacketWriter(Action62.Code, Action62.BedStore).U16(furn).U8(bed).U8(id);
        WriteRecord(w, pet);
        return w.ToArray();
    }

    /// <summary>Writes a pet as the fixed-size stored-pet record of 339 bytes.</summary>
    /// <param name="w">The packet writer to append to.</param>
    /// <param name="pet">The pet.</param>
    public static void WriteRecord(PacketWriter w, Pet pet)
    {
        var start = w.Length;
        w.U16(pet.NpcId);
        var name = GameEncoding.GetBytes(pet.Name);
        w.U8((byte)Math.Min(20, name.Length)).Bytes(name.AsSpan(0, Math.Min(20, name.Length)));
        w.Bytes(new byte[20 - Math.Min(20, name.Length)]);
        w.U16((ushort)Math.Min(ushort.MaxValue, pet.Hp)).U16((ushort)Math.Min(ushort.MaxValue, pet.Sp))
            .U16(pet.Strength).U16(pet.Constitution).U16(pet.Intelligence).U16(pet.Wisdom).U16(pet.Agility)
            .U8(pet.Level).I32(pet.Exp).U16(pet.SkillPoints).U8(0).U8(pet.Amity);
        for (var i = 0; i < 4; i++)
            w.U8(i < pet.SkillLevels.Length ? pet.SkillLevels[i] : (byte)0).I32(i < pet.SkillExp.Length ? pet.SkillExp[i] : 0);
        for (byte part = 0; part < 7; part++)
        {
            var e = pet.Equipment.Find(x => x.Slot == part);
            w.U16(e?.ItemId ?? 0)
                .U8(e?.Wear ?? 0)
                .Bytes(new byte[5])
                .U8(0)
                .U8(0)
                .U16(e?.Insert ?? 0)
                .F64(0)
                .U8(e?.Fusion ?? 0)
                .U8(0)
                .U16(e?.Pose ?? 0)
                .Bytes(new byte[10]);
        }
        w.U8(pet.Tradable).Bytes(new byte[3]).U8(pet.TrainPercent).Bytes(new byte[2]).U8(pet.Reborn).U8(pet.Potential).Bytes(new byte[26]);
        if (w.Length - start != 339) // TStoredPetRec
            throw new InvalidOperationException($"stored pet record is {w.Length - start} bytes");
    }
}
