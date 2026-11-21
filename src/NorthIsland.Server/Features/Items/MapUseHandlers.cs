using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using NorthIsland.Protocol.Messages.Items;
using NorthIsland.Protocol.Messages.Player;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.Features.Items;

/// <summary>Using items and heal skills on the map (outside battle) from the hotkeys and the skill window, on the player or a pet.</summary>
/// <param name="items">Item definitions.</param>
/// <param name="skills">Skill definitions.</param>
/// <param name="npcs">NPC definitions, for the skills of a pet.</param>
/// <param name="world">The online players, to find the target of a cast.</param>
/// <param name="logger">Logger.</param>
public sealed class MapUseHandlers(
    ItemData items,
    SkillData skills,
    NpcData npcs,
    WorldState world,
    ILogger<MapUseHandlers> logger)
{
    /// <summary>Who an item or skill is used on: a player, or one of their pets.</summary>
    /// <param name="Session">The session of the player, or of the pet's owner.</param>
    /// <param name="Pet">The pet, or null when the target is the player.</param>
    private sealed record Target(
        GameSession Session,
        Pet? Pet)
    {
        /// <summary>The name of the pet, or of the player.</summary>
        public string Name => Pet?.Name ?? Session.ActiveCharacter!.Name;
    }

    /// <summary>Handles a hotkey use of an item or a heal skill on the player or one of their pets.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: kind (1 item, 2 skill), id, caster, target kind (4 for a pet), target pet slot and the target id.</param>
    internal ValueTask OnHotkeyUse(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || session.ActiveCharacter is not { } character || body.Length < 10 || session.Battle is not null)
            return ValueTask.CompletedTask;
        var s = body.Span;
        var (kind, id, caster, targetKind, targetSlot) = (s[0],
            BinaryPrimitives.ReadUInt16LittleEndian(s[1..]), // 1 id
            s[3], // 3 caster
            s[4], // 4 targetKind
            s[5]); // 5 targetSlot
        var target = targetKind == 4
            ? character.Pets.Find(p => p.Slot == targetSlot) is { } pet ? new Target(session, pet) : null
            : new Target(session, null);
        if (target is null)
        {
            logger.LogWarning("[{Session}] hotkey use: no target (kind {Kind}, pet slot {Slot})", session.Id, targetKind, targetSlot);
            return ValueTask.CompletedTask;
        }
        if (kind == 1)
            UseItem(session, character, id, target);
        else if (kind == 2)
            CastHeal(session, character, caster, id, target);
        return ValueTask.CompletedTask;
    }

    /// <summary>Handles casting a heal skill from the skill window on a player or a pet.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: caster, target kind, target id, owner id (for pets) and skill id.</param>
    internal ValueTask OnCast(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || session.ActiveCharacter is not { } character || body.Length < 12 || session.Battle is not null)
            return ValueTask.CompletedTask;
        var s = body.Span;
        var caster = s[0];
        var targetId = BinaryPrimitives.ReadInt32LittleEndian(s[2..]);
        var ownerId = BinaryPrimitives.ReadInt32LittleEndian(s[6..]);
        var skillId = BinaryPrimitives.ReadUInt16LittleEndian(s[10..]);
        var myId = session.Account!.LoginId;
        Target? target = null;
        if (ownerId != 0)
        {
            if ((ownerId == myId ? session : world.SessionOf(ownerId)) is { ActiveCharacter: { } oc } ownerSession
                && oc.Pets.Find(p => p.NpcId == targetId) is { } pet)
                target = new Target(ownerSession, pet);
        }
        else if (targetId == myId)
            target = new Target(session, null);
        else if (world.SessionOf(targetId) is { } other && other.ActiveCharacter is not null)
            target = new Target(other, null);
        if (target is null)
        {
            logger.LogWarning("[{Session}] cast {Skill}: target {Id} (owner {Owner}) not found", session.Id, skillId, targetId, ownerId);
            return ValueTask.CompletedTask;
        }
        CastHeal(session, character, caster, skillId, target);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The farthest a heal may reach another player or their pet on each axis, in pixels: the client sets no reach for a map heal, so this is
    /// its pick-up reach.
    /// </summary>
    private const int CastReach = 170;

    /// <summary>
    /// Uses the first stack of an item in the bag that is not committed elsewhere (see <see cref="BagLocks"/>) on the target: restores its HP and SP
    /// and takes one. A quest pet is not fed.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's character.</param>
    /// <param name="itemId">The item id.</param>
    /// <param name="target">Who the item is used on.</param>
    private void UseItem(GameSession session, Character character, ushort itemId, Target target)
    {
        var stack = character.Bag.Where(b => b.ItemId == itemId && !BagLocks.IsLocked(session, b.Slot)).OrderBy(b => b.Slot).FirstOrDefault();
        if (stack is null || items[itemId] is not { Restores.Count: > 0 } item)
        {
            logger.LogWarning("[{Session}] hotkey item {Item}: not in the bag (or only committed elsewhere) or no known effect", session.Id, itemId);
            return;
        }
        if (target.Pet is { Tradable: 0 })
        {
            logger.LogWarning("[{Session}] hotkey item {Item} on pet {Pet}: a quest NPC", session.Id, itemId, target.Name);
            return;
        }
        var hp = item.Restores.Where(r => r.Attr == Action8.AttrHp).Sum(r => r.Amount);
        var sp = item.Restores.Where(r => r.Attr == Action8.AttrSp).Sum(r => r.Amount);
        Restore(target, hp, sp);
        stack.Count--;
        if (stack.Count == 0)
            character.Bag.Remove(stack);
        session.Send(ItemMessages.BagSlotDecrease(stack.Slot, 1));
        session.Send(ItemMessages.UsedSound());
        logger.LogInformation("[{Session}] {Name} uses {Item} on {Target} (+{Hp} HP, +{Sp} SP)", session.Id, character.Name, item.Name, target.Name, hp, sp);
    }

    /// <summary>
    /// Casts a heal skill the caster knows and has the SP for, paying the SP and healing the target.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's character.</param>
    /// <param name="caster">0 when the player casts, otherwise the slot of the casting pet.</param>
    /// <param name="skillId">The skill id.</param>
    /// <param name="target">Who is healed.</param>
    private void CastHeal(
        GameSession session,
        Character character,
        byte caster,
        ushort skillId,
        Target target)
    {
        var skill = skills[skillId];
        var pet = caster == 0 ? null : character.Pets.Find(p => p.Slot == caster);
        var knows = caster == 0
            ? character.Skill(skillId) is not null
            : pet is not null && npcs[pet.NpcId] is { } npc
                && Enumerable.Range(0, Math.Min(3, npc.SkillSlots.Count)).Any(i => npc.SkillSlots[i] == skillId && pet.SkillLevels.ElementAtOrDefault(i) > 0);
        var casterSp = pet?.Sp ?? character.Sp;
        if (skill is null || !knows || skill.Type is not (6 or 7 or 14) || casterSp < skill.SpCost || (caster != 0 && pet is null))
        {
            logger.LogWarning("[{Session}] {Name}: cannot cast {Skill} on the map (known {Known}, type {Type}, SP {Sp})", session.Id,
                character.Name, skillId, knows, skill?.Type, casterSp);
            return;
        }
        if (OutOfReach(session, character, target) is { } far)
        {
            logger.LogWarning("[{Session}] {Name}: cannot cast {Skill} on {Target}: {Why}", session.Id, character.Name, skillId, target.Name, far);
            return;
        }
        var amount = Math.Max(1, skill.Power + 2 * (pet?.Intelligence ?? character.Intelligence) + 2 * (pet?.Level ?? character.Level));
        if (pet is not null)
        {
            pet.Sp -= skill.SpCost;
            session.Send(PetMessages.SetAttribute(pet.Slot, Action8.AttrSp, pet.Sp));
        }
        else
        {
            character.Sp -= skill.SpCost;
            session.Send(InnMessages.SetAttribute(Action8.AttrSp, character.Sp));
        }
        Restore(target, skill.Type is 7 or 14 ? amount : 0, skill.Type is 6 or 14 ? amount : 0);
        logger.LogInformation(
            "[{Session}] {Caster} casts {Skill} on {Target} ({Amount})",
            session.Id,
            pet?.Name ?? character.Name,
            skill.Name,
            target.Name,
            amount);
    }

    /// <summary>
    /// Tells why a heal cannot reach another player or their pet: not on the caster's map, farther than <see cref="CastReach"/>, or in a battle.
    /// </summary>
    /// <param name="session">The caster's session.</param>
    /// <param name="character">The caster's character.</param>
    /// <param name="target">Who is healed.</param>
    /// <returns>The reason for the log, or null when the target is the caster's own or within reach.</returns>
    private static string? OutOfReach(GameSession session, Character character, Target target)
    {
        if (ReferenceEquals(target.Session, session))
            return null;
        if (target.Session.ActiveCharacter is not { } other || other.MapId != character.MapId)
            return "not on the same map";
        if (Math.Abs(other.X - character.X) > CastReach || Math.Abs(other.Y - character.Y) > CastReach)
            return $"too far away at ({other.X}, {other.Y}), the caster is at ({character.X}, {character.Y})";
        return target.Session.Battle is not null ? "in a battle" : null;
    }

    /// <summary>Adds HP and SP to the target up to its maximum and sends the new values.</summary>
    /// <param name="target">Who is healed.</param>
    /// <param name="hp">The HP to add.</param>
    /// <param name="sp">The SP to add.</param>
    private static void Restore(Target target, int hp, int sp)
    {
        if (target.Pet is { } pet)
        {
            if (hp > 0)
            {
                pet.Hp = Math.Min(pet.MaxHp, pet.Hp + hp);
                target.Session.Send(PetMessages.SetAttribute(pet.Slot, Action8.AttrHp, pet.Hp));
            }
            if (sp > 0)
            {
                pet.Sp = Math.Min(pet.MaxSp, pet.Sp + sp);
                target.Session.Send(PetMessages.SetAttribute(pet.Slot, Action8.AttrSp, pet.Sp));
            }
            return;
        }
        var c = target.Session.ActiveCharacter!;
        if (hp > 0)
        {
            c.Hp = Math.Min(c.MaxHp, c.Hp + hp);
            target.Session.Send(InnMessages.SetAttribute(Action8.AttrHp, c.Hp));
        }
        if (sp > 0)
        {
            c.Sp = Math.Min(c.MaxSp, c.Sp + sp);
            target.Session.Send(InnMessages.SetAttribute(Action8.AttrSp, c.Sp));
        }
    }
}
