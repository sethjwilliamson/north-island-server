using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using NorthIsland.Protocol.Messages.Player;
using NorthIsland.Protocol.Opcodes;
using NorthIsland.Protocol.Text;

namespace NorthIsland.Server.Features.PetCare;

/// <summary>Handles the actions on a player's own pets: riding, learning skills, renaming, exiling, joining combat and resting.</summary>
/// <param name="world">The world state, for broadcasts and the pet's appearance on the map.</param>
/// <param name="npcs">The NPC data, for the pets' skill slots.</param>
/// <param name="skills">The skill data, for learn costs.</param>
/// <param name="combat">The combat stats, resent when riding changes.</param>
/// <param name="battles">The battle handlers, which take a pet called into a battle.</param>
/// <param name="logger">The logger.</param>
public sealed class PetHandlers(
    World.WorldState world,
    NpcData npcs,
    SkillData skills,
    World.CombatStats combat,
    BattleHandlers battles,
    ILogger<PetHandlers> logger)
{
    /// <summary>
    /// Mounts a pet, and tells the player and the map. The pet rests first when it is the active pet, as the client asks before riding;
    /// riding is refused on a vehicle, while keeping a stall and while a pet trade is open.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the pet's slot, then its NPC id.</param>
    internal ValueTask OnRide(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (PetOf(session, body) is not (var character, var pet) || body.Length < 5 || session.Battle is not null)
            return ValueTask.CompletedTask;
        var why = BinaryPrimitives.ReadInt32LittleEndian(body.Span[1..]) != pet.NpcId ? $"the request names another pet than slot {pet.Slot}"
            : session.Vehicle is { Riding: true } ? "rides a vehicle"
                : session.Stalls.Any ? "keeps a stall"
                    : session.Trades.Pets is not null ? "a pet trade is open"
                        : null;
        if (why is not null)
        {
            logger.LogWarning("[{Session}] {Name}: ride on pet {Pet} refused ({Why})", session.Id, character.Name, pet.Name, why);
            return ValueTask.CompletedTask;
        }
        if (character.ActivePetId == pet.NpcId)
            RestActive(session, character);
        session.RidingPetSlot = pet.Slot;
        var packet = PetMessages.Mounted(pet.Slot, session.Account!.LoginId, pet.NpcId);
        session.Send(packet);
        world.Broadcast(session, packet, sameMapOnly: true);
        combat.Send(session);
        logger.LogInformation("[{Session}] {Name} rides pet {Pet} ({Id})", session.Id, character.Name, pet.Name, pet.NpcId);
        return ValueTask.CompletedTask;
    }

    /// <summary>Gets the player off the pet they ride, and tells the player and the map.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body, which is not read.</param>
    internal ValueTask OnLeaveRide(GameSession session, ReadOnlyMemory<byte> body)
    {
        var character = session.ActiveCharacter;
        if (session.State != SessionState.InWorld || character is null)
            return ValueTask.CompletedTask;
        Dismount(session, character);
        return ValueTask.CompletedTask;
    }

    /// <summary>Gets the player off the pet they ride, and tells the player and the map.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's character.</param>
    private void Dismount(GameSession session, Character character)
    {
        session.RidingPetSlot = 0;
        var packet = PetMessages.Dismounted(session.Account!.LoginId);
        session.Send(packet);
        world.Broadcast(session, packet, sameMapOnly: true);
        combat.Send(session);
        logger.LogInformation("[{Session}] {Name} gets off the pet", session.Id, character.Name);
    }

    /// <summary>Teaches a pet a skill for skill points.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the skill id and the level to learn.</param>
    internal ValueTask OnLearn(GameSession session, ReadOnlyMemory<byte> body)
    {
        var character = session.ActiveCharacter;
        if (session.State != SessionState.InWorld || character is null || body.Length < 3)
            return ValueTask.CompletedTask;
        var skillId = BinaryPrimitives.ReadUInt16LittleEndian(body.Span);
        var level = body.Span[2];
        var skill = skills[skillId];
        var candidates = character.Pets.OrderByDescending(p => p.NpcId == character.ActivePetId)
            .Select(p => (Pet: p, Index: npcs[p.NpcId]?.SkillSlots.ToList().IndexOf(skillId) ?? -1))
            .Where(c => c.Index is >= 0 and < 3 && c.Pet.SkillLevels.ElementAtOrDefault(c.Index) == 0);
        var (pet, index) = candidates.FirstOrDefault();
        var cost = skill?.LearnCost ?? 0;
        if (skill is null || pet is null || level != 1 || character.SkillPoints < cost)
        {
            logger.LogWarning("[{Session}] {Name}: cannot learn skill {Skill} level {Level} ({Why})", session.Id, character.Name, skillId, level,
                skill is null
                    ? "unknown skill"
                    : pet is null
                        ? "no pet with it at level 0"
                        : level != 1 ? "only level 1 is learned" : $"{character.SkillPoints} skill points, needs {cost}");
            return ValueTask.CompletedTask;
        }
        pet.SkillLevels[index] = 1;
        character.SkillPoints -= cost;
        session.Send(PetMessages.SetAttribute(pet.Slot, Action8.AttrSkillLevel, 1, skillId));
        session.Send(InnMessages.SetAttribute(Action8.AttrSkillPoints, character.SkillPoints));
        logger.LogInformation("[{Session}] {Name}: pet {Pet} learns {Skill} ({Id}) for {Cost} skill point(s) ({Left} left)", session.Id,
            character.Name, pet.Name, skill.Name, skillId, cost, character.SkillPoints);
        return ValueTask.CompletedTask;
    }

    /// <summary>The longest pet name, in bytes: the name field of the stored-pet record.</summary>
    private const int MaxNameLength = 20;

    /// <summary>
    /// Renames a pet that can be traded (the client offers the rename only for those) to a name of 1 to 20 printable bytes; control bytes
    /// and DEL, which the client's text box never takes, refuse the name.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the pet's slot, then the new name.</param>
    internal ValueTask OnRename(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (PetOf(session, body) is not (var character, var pet))
            return ValueTask.CompletedTask;
        var raw = body.Span[1..];
        var name = GameEncoding.GetString(raw).Trim();
        var printable = raw.IndexOfAnyInRange((byte)0x00, (byte)0x1F) < 0 && !raw.Contains((byte)0x7F); // 0x00..0x1F control bytes, 0x7F DEL
        if (name.Length is 0 or > MaxNameLength || !printable || pet.Tradable != 1)
        {
            logger.LogWarning("[{Session}] {Name}: refused pet name '{PetName}' for pet {Pet} (tradable {Tradable})", session.Id, character.Name,
                name.ReplaceLineEndings(" "), pet.Name, pet.Tradable);
            return ValueTask.CompletedTask;
        }
        logger.LogInformation("[{Session}] {Name} renames pet {Old} ({Id}) to {New}", session.Id, character.Name, pet.Name, pet.NpcId, name);
        pet.Name = name;
        session.Send(PetMessages.Renamed(session.Account!.LoginId, pet.Slot, name));
        return ValueTask.CompletedTask;
    }

    /// <summary>Sends a pet away for good; refused while a pet trade is open or the pet is on sale in the player's pet stall.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the pet's slot.</param>
    internal ValueTask OnExile(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (PetOf(session, body) is not (var character, var pet) || session.Battle is not null)
            return ValueTask.CompletedTask;
        if (session.Trades.Pets is not null || session.Stalls.Pets?.Prices.ContainsKey(pet.Slot) == true)
        {
            logger.LogWarning("[{Session}] {Name}: exile of pet {Pet} refused ({Why})", session.Id, character.Name, pet.Name,
                session.Trades.Pets is not null ? "a pet trade is open" : "the pet is in his stall");
            return ValueTask.CompletedTask;
        }
        Pets.Exile(session, character, pet, world, combat);
        logger.LogInformation("[{Session}] {Name} exiles pet {Pet} ({Id}) from slot {Slot}", session.Id, character.Name, pet.Name, pet.NpcId, pet.Slot);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Makes a pet the active pet that fights beside the player, and shows it on the map. A ridden pet gets the player off it first: the
    /// client's fight command sends the join before the dismount, and a pet is never ridden and active at once.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the pet's NPC id.</param>
    internal ValueTask OnJoin(GameSession session, ReadOnlyMemory<byte> body)
    {
        var character = session.ActiveCharacter;
        if (session.State != SessionState.InWorld || character is null || body.Length < 4)
            return ValueTask.CompletedTask;
        var npcId = BinaryPrimitives.ReadInt32LittleEndian(body.Span);
        var pet = character.Pets.Find(p => p.NpcId == npcId);
        if (pet is not null && session.Battle is not null)
        {
            battles.TrySummonPet(session, character, pet);
            return ValueTask.CompletedTask;
        }
        if (pet is null || session.Battle is not null)
        {
            logger.LogWarning("[{Session}] {Name}: pet {Id} cannot join{Why}", session.Id, character.Name, npcId,
                pet is null ? " (not owned)" : " during a battle");
            return ValueTask.CompletedTask;
        }
        if (session.RidingPetSlot == pet.Slot)
            Dismount(session, character);
        character.ActivePetId = pet.NpcId;
        session.Send(PetMessages.Active(pet.NpcId));
        world.ShowPet(session);
        logger.LogInformation("[{Session}] {Name}: pet {Pet} ({Id}) joins combat", session.Id, character.Name, pet.Name, pet.NpcId);
        return ValueTask.CompletedTask;
    }

    /// <summary>Rests the active pet and hides it from the map.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body, which is not read.</param>
    internal ValueTask OnRest(GameSession session, ReadOnlyMemory<byte> body)
    {
        var character = session.ActiveCharacter;
        if (session.State != SessionState.InWorld || character is null || session.Battle is not null)
            return ValueTask.CompletedTask;
        RestActive(session, character);
        return ValueTask.CompletedTask;
    }

    /// <summary>Rests the active pet, tells the player and hides the pet from the map.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's character.</param>
    private void RestActive(GameSession session, Character character)
    {
        logger.LogInformation("[{Session}] {Name}: pet {Id} rests", session.Id, character.Name, character.ActivePetId);
        character.ActivePetId = 0;
        session.Send(PetMessages.Rest());
        world.HidePet(session);
    }

    /// <summary>Finds the pet in the slot a packet names, and logs a warning when there is none.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body, whose first byte is the pet's slot.</param>
    /// <returns>The character and the pet, or null when the player is not in the world, the body is empty or the slot holds no pet.</returns>
    private (Character Character, Pet Pet)? PetOf(GameSession session, ReadOnlyMemory<byte> body)
    {
        var character = session.ActiveCharacter;
        if (session.State != SessionState.InWorld || character is null || body.Length < 1)
            return null;
        var slot = body.Span[0];
        if (character.Pets.Find(p => p.Slot == slot) is not { } found)
        {
            logger.LogWarning("[{Session}] {Name}: no pet in slot {Slot}", session.Id, character.Name, slot);
            return null;
        }
        return (character, found);
    }
}
