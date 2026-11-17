using Microsoft.Extensions.Logging;
using NorthIsland.GameData.Characters;
using NorthIsland.Protocol.Messages.Items;

namespace NorthIsland.Server.Features.Events;

public sealed partial class EventEngine
{
    /// <summary>Records a role change to apply later, unless the role has no body and head.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The character.</param>
    /// <param name="run">The running event, for the log.</param>
    /// <param name="role">The role number.</param>
    private void ChangeRole(GameSession session, Character character, EventRun run, byte role)
    {
        if (RoleData.SplitRole(role) is null)
        {
            logger.LogWarning("[{Session}] {Source}: role {Role} has no body / head in the client, skipped", session.Id, run.Source, role);
            return;
        }
        character.PendingRole = role;
        logger.LogInformation("[{Session}] {Source}: {Name} becomes role {Role} when he next leaves the map", session.Id, run.Source, character.Name, role);
    }

    /// <summary>Applies the character's pending role change as they leave the map and tells them and the players online.</summary>
    /// <param name="session">The player's session.</param>
    public void ApplyPendingRole(GameSession session)
    {
        if (session.ActiveCharacter is not { PendingRole: not 0 } character || session.Account is null)
            return;
        var role = character.PendingRole;
        var before = RoleData.RoleIndex(character.BodyType, character.Head);
        if (TakeRole(character, role) is not var (destroyed, worn))
            return;
        var playerId = session.Account.LoginId;
        var change = Protocol.Messages.Player.PlayerMessages.RoleChange(playerId, role);
        session.Send(change);
        world.Broadcast(session, change, sameMapOnly: false);
        var others = new List<byte[]>();
        foreach (var gone in destroyed)
        {
            session.Send(ItemMessages.EquipSlotCleared(gone.Slot));
            others.Add(ItemMessages.PlayerEquipChange(playerId, gone.ItemId, on: false));
        }
        if (worn.Count > 0)
            session.Send(
                ItemMessages.EquipList(
                    worn.Select(e => new EquipEntry { ItemId = e.ItemId, Wear = e.Wear, Insert = e.Insert, FusionLevel = e.Fusion, LookItemId = e.Pose })));
        others.AddRange(worn.Select(e => ItemMessages.PlayerEquipChange(playerId, e.ItemId, on: true)));
        combat.Send(session);
        foreach (var packet in others)
            world.Broadcast(session, packet, sameMapOnly: false);
        logger.LogInformation(
            "[{Session}] {Name} leaves the map as role {Role} (was {Before}): {Destroyed} destroyed, now wears {Items}",
            session.Id,
            character.Name,
            role,
            before,
            destroyed.Count == 0 ? "nothing" : string.Join(", ", destroyed.Select(e => e.ItemId)), string.Join(", ", character.EquippedItemIds()));
    }

    /// <summary>Applies the character's pending role change at login, without telling anyone.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The character.</param>
    public void TakePendingRole(GameSession session, Character character)
    {
        if (character.PendingRole == 0)
            return;
        var role = character.PendingRole;
        if (TakeRole(character, role) is var (destroyed, _))
            logger.LogInformation(
                "[{Session}] {Name} logs in as role {Role} ({Count} worn item(s) destroyed)",
                session.Id,
                character.Name,
                role,
                destroyed.Count);
    }

    /// <summary>Gives the character the body and head of a role and its starter set in place of everything they wore.</summary>
    /// <param name="character">The character.</param>
    /// <param name="role">The role number.</param>
    /// <returns>The pieces that were worn and destroyed, and the pieces now worn; null when the role has no body and head.</returns>
    private (List<EquippedItem> Destroyed, List<EquippedItem> Worn)? TakeRole(Character character, byte role)
    {
        character.PendingRole = 0;
        if (RoleData.SplitRole(role) is not { } look)
            return null;
        (character.BodyType, character.Head) = look;
        var destroyed = character.Equipment.OrderBy(e => e.Slot).ToList();
        character.Equipment.Clear();
        var worn = new List<EquippedItem>();
        foreach (var itemId in RoleData.DefaultEquipment(role))
        {
            var slot = items[itemId]?.EquipSlot ?? 0;
            if (slot is < 1 or > 6 || worn.Exists(e => e.Slot == slot))
                continue;
            var piece = new EquippedItem { Slot = slot, ItemId = itemId };
            character.Equipment.Add(piece);
            worn.Add(piece);
        }
        return (destroyed, worn);
    }

    /// <summary>Makes a team NPC join the character's team as a pet, or leave it.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The character.</param>
    /// <param name="run">The running event, whose hire tier decides whether the NPC joins on a lease.</param>
    /// <param name="npcId">The NPC id.</param>
    /// <param name="joins">True to join, false to leave.</param>
    public void TeamNpc(
        GameSession session,
        Character character,
        EventRun run,
        ushort npcId,
        bool joins)
    {
        if (joins)
        {
            if (npcs[npcId] is not { } npc || character.Pets.Count >= Pet.MaxPets || character.Pets.Exists(p => p.NpcId == npcId))
            {
                logger.LogWarning("[{Session}] {Source}: NPC {Npc} cannot join (team full, already there or unknown)", session.Id, run.Source, npcId);
                return;
            }
            var returning = character.DepartedTeamNpcs.Find(p => p.NpcId == npcId);
            var pet = returning ?? Pets.Create(npc, Pets.FreeSlot(character), formulas.Table, level: 1);
            if (returning is not null)
            {
                character.DepartedTeamNpcs.Remove(returning);
                returning.Slot = Pets.FreeSlot(character);
            }
            pet.LeaseUntil = run.HireTier != 0 ? HireTiers.LeaseEnd(TimedEffects.Now, options.Value.HireLeaseHours) : null;
            character.Pets.Add(pet);
            Pets.GiveStartingGear(pet, options.Value.TeamNpcEquipment, id => items[id]);
            // The join packet carries the weapon: a client that already has the pet ignores it in a later pet list.
            var weapon = pet.Equipment.Find(e => e.Slot == Gems.WeaponSlot)?.ItemId ?? 0;
            session.Send(Protocol.Messages.Player.PetMessages.Joined(session.Account!.LoginId, Pets.Info(pet), weapon));
            if (pet.Equipment.Count > 0)
            {
                session.Send(Protocol.Messages.Player.PetMessages.List([Pets.Info(pet)], session.Client));
                combat.SendPetBonuses(session, pet);
            }
            logger.LogInformation("[{Session}] {Source}: {Pet} ({Npc}) {What} the team in slot {Slot} (level {Level})", session.Id, run.Source,
                pet.Name, npcId, returning is null ? "joins" : "rejoins", pet.Slot, pet.Level);
            return;
        }
        if (character.Pets.Find(p => p.NpcId == npcId) is { } leaving)
        {
            PetTradeHandlers.Give(world, session, character, leaving);
            character.DepartedTeamNpcs.RemoveAll(p => p.NpcId == npcId);
            character.DepartedTeamNpcs.Add(leaving);
            logger.LogInformation("[{Session}] {Source}: {Pet} ({Npc}) leaves the team (level {Level} kept for a return)", session.Id, run.Source,
                leaving.Name, npcId, leaving.Level);
        }
    }

    /// <summary>
    /// Changes the amity of a team NPC (a quest reward): the NPC in the team, else the one in storage or the one that left the team,
    /// kept between 0 and the amity ceiling. The client hears of it when the NPC is in the team.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The character.</param>
    /// <param name="run">The running event, for the log.</param>
    /// <param name="npcId">The NPC id.</param>
    /// <param name="change">The amity to add; negative to take.</param>
    public void TeamNpcAmity(GameSession session, Character character, EventRun run, ushort npcId, int change)
    {
        var pet = character.Pets.Find(p => p.NpcId == npcId)
            ?? character.StoredPets.Find(p => p.NpcId == npcId)
            ?? character.DepartedTeamNpcs.Find(p => p.NpcId == npcId);
        if (pet is null)
        {
            logger.LogInformation("[{Session}] {Source}: NPC {Npc} amity {Change:+0;-0}: the player doesn't have it", session.Id, run.Source, npcId, change);
            return;
        }
        var max = Math.Clamp(itemOptions.Value.ItemUse.MaxAmity, 1, byte.MaxValue);
        pet.Amity = (byte)Math.Clamp(pet.Amity + change, 0, Math.Max(max, (int)pet.Amity));
        if (character.Pets.Contains(pet))
            session.Send(Protocol.Messages.Player.PetMessages.SetAttribute(pet.Slot, Protocol.Opcodes.Action8.AttrAmity, pet.Amity));
        logger.LogInformation("[{Session}] {Source}: {Pet} ({Npc}) amity {Change:+0;-0}, now {Amity}", session.Id, run.Source, pet.Name, npcId, change, pet.Amity);
    }

    /// <summary>Charges the account's mall points for a hire tier when the character's level and the points allow it.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The character.</param>
    /// <param name="run">The running event, for the log.</param>
    /// <param name="value">The tier number.</param>
    /// <returns>The tier that was charged; 0 when it is unknown or refused.</returns>
    private int Hire(GameSession session, Character character, EventRun run, int value)
    {
        if (HireTiers.Find(value) is not { } tier)
        {
            logger.LogInformation("[{Session}] {Source}: charge tier {Tier} is not known, refused", session.Id, run.Source, value);
            return 0;
        }
        var account = session.Account!;
        if (character.Level < tier.MinLevel || account.MallPoints < tier.Points)
        {
            logger.LogInformation("[{Session}] {Source}: tier {Tier} refused (level {Level} of {MinLevel}, {Points} points of {Cost})", session.Id, run.Source,
                value, character.Level, tier.MinLevel, account.MallPoints, tier.Points);
            return 0;
        }
        account.MallPoints -= tier.Points;
        session.Send(MallMessages.Points(account.MallPoints));
        logger.LogInformation("[{Session}] {Source}: tier {Tier} charged, {Points} IM points left", session.Id, run.Source, value, account.MallPoints);
        return value;
    }

}
