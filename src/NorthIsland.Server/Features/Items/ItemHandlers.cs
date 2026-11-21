using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NorthIsland.GameData.Characters;
using NorthIsland.Protocol.IO;
using NorthIsland.Protocol.Messages.Items;
using NorthIsland.Protocol.Messages.Player;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.Features.Items;

/// <summary>
/// The bag and equipment actions: equipping and unequipping for the player and their pets, moving, picking up, dropping and discarding items,
/// throwing gold, and using items.
/// </summary>
/// <param name="items">Item definitions.</param>
/// <param name="npcs">NPC definitions, for the career pets that may wear the gear of the pet they grew from.</param>
/// <param name="world">The online players, to show equipment changes to others.</param>
/// <param name="ground">The items on the ground of each map.</param>
/// <param name="bottles">The bottle handlers, for pouring a potion into a bottle by moving it.</param>
/// <param name="tents">The tent handlers, for using a tent item.</param>
/// <param name="combat">Combat stats, resent after an equipment change.</param>
/// <param name="effects">The item effect handlers, asked first when an item is used.</param>
/// <param name="marriages">The marriages, to refuse equipment changes during a ceremony.</param>
/// <param name="options">Item settings.</param>
/// <param name="logger">Logger.</param>
public sealed class ItemHandlers(
    ItemData items,
    NpcData npcs,
    WorldState world,
    GroundItems ground,
    BottleHandlers bottles,
    TentHandlers tents,
    CombatStats combat,
    ItemEffectHandlers effects,
    MarriageRegistry marriages,
    IOptions<ItemOptions> options,
    ILogger<ItemHandlers> logger)
{
    /// <summary>Gives a new character the starter kit once: the role's default outfit and the configured equipment and bag items.</summary>
    /// <param name="character">The character.</param>
    public void EnsureStarterKit(Character character)
    {
        var kit = options.Value.StarterKit;
        if (!kit.Enabled || character.StarterKitGiven)
            return;
        var outfit = RoleData.DefaultEquipment(RoleData.RoleIndex(character.BodyType, character.Head));
        foreach (var id in outfit.Concat(kit.Equip))
        {
            var slot = items[id]?.EquipSlot ?? 0;
            if (slot is >= 1 and <= 6 && character.Equipment.TrueForAll(e => e.Slot != slot))
                character.Equipment.Add(new EquippedItem { Slot = slot, ItemId = id });
            else
                AddToBag(character, id);
        }
        foreach (var id in kit.Bag)
            AddToBag(character, id);
        character.StarterKitGiven = true;
        logger.LogInformation("{Name} received the starter kit", character.Name);
    }

    /// <summary>Sends the worn equipment and the bag to the player.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's character.</param>
    public static void SendInventory(GameSession session, Character character)
    {
        session.Send(ItemMessages.EquipList(character.Equipment.OrderBy(e => e.Slot)
            .Select(e => new EquipEntry { ItemId = e.ItemId, Wear = e.Wear, Insert = e.Insert, FusionLevel = e.Fusion, LookItemId = e.Pose })));
        session.Send(ItemMessages.BagList(character.Bag.OrderBy(b => b.Slot)
            .Select(
                b =>
                    new BagEntry
                    {
                        Slot = b.Slot,
                        ItemId = b.ItemId,
                        Count = b.Count,
                        Wear = b.Wear,
                        Insert = b.Insert,
                        FusionLevel = b.Fusion,
                        PoseItemId = b.Pose,
                        BoxItemId = b.BoxItem,
                        UsedAmount = b.Used,
                        ExtraAmount = b.Extra
                    })));
    }

    /// <summary>Handles wearing an item from a bag slot; a piece already worn in that slot goes back to the bag slot.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the bag slot.</param>
    internal ValueTask OnEquip(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (CharacterOf(session) is not { } character)
            return ValueTask.CompletedTask;
        if (marriages.InCeremony(session.Account!.LoginId))
        {
            session.Send(MarriageMessages.Result(MarriageMessages.NoEquipChange));
            return ValueTask.CompletedTask;
        }
        var bagSlot = EquipRequest.Parse(body.Span).BagSlot;
        var bagItem = character.Bag.Find(b => b.Slot == bagSlot);
        var record = bagItem is null ? null : items[bagItem.ItemId];
        var equipSlot = record?.EquipSlot ?? 0;
        if (bagItem is null || equipSlot is < 1 or > 6)
        {
            logger.LogWarning("[{Session}] equip from bag slot {Slot}: no equipment there", session.Id, bagSlot);
            return ValueTask.CompletedTask;
        }
        if (BagLocks.LockedBy(session, bagSlot) is { } locked)
        {
            logger.LogWarning("[{Session}] equip from bag slot {Slot}: {Item} is {Why}", session.Id, bagSlot, bagItem.ItemId, locked);
            return ValueTask.CompletedTask;
        }
        if (record!.RequiredLevel > character.Level)
        {
            logger.LogWarning("[{Session}] {Name} (level {Level}) cannot wear {Item}, needs level {Required}",
                session.Id, character.Name, character.Level, bagItem.ItemId, record.RequiredLevel);
            return ValueTask.CompletedTask;
        }
        if (!CanWear(record, character))
        {
            logger.LogWarning("[{Session}] {Name} (body type {Body}, element {Element}) cannot wear {Item}: usable by {UsableBy}, pet {Pet}",
                session.Id, character.Name, character.BodyType, character.Element, bagItem.ItemId, record.UsableBy, record.PetNpcId);
            return ValueTask.CompletedTask;
        }

        var worn = character.Equipment.Find(e => e.Slot == equipSlot);
        if (worn is not null && !FitsInPlace(character, bagItem, worn.ItemId))
        {
            logger.LogWarning("[{Session}] equip from bag slot {Slot}: the worn {Worn} does not fit back into the slot of {Item}", session.Id,
                bagSlot, worn.ItemId, bagItem.ItemId);
            return ValueTask.CompletedTask;
        }
        TakeFromBag(character, bagItem, 1);
        if (worn is not null)
        {
            character.Equipment.Remove(worn);
            character.Bag.Add(
                new BagItem
                {
                    Slot = bagSlot,
                    ItemId = worn.ItemId,
                    Wear = worn.Wear,
                    Insert = worn.Insert,
                    Fusion = worn.Fusion,
                    Pose = worn.Pose,
                    BoxItem = worn.BoxItem
                });
        }
        character.Equipment.Add(
            new EquippedItem
            {
                Slot = equipSlot,
                ItemId = bagItem.ItemId,
                Wear = bagItem.Wear,
                Insert = bagItem.Insert,
                Fusion = bagItem.Fusion,
                Pose = bagItem.Pose
            });

        combat.Send(session);
        session.Send(ItemMessages.EquipFromBag(bagSlot, bagSlot));
        world.Broadcast(session, ItemMessages.PlayerEquipChange(session.Account!.LoginId, bagItem.ItemId, on: true), sameMapOnly: false);
        logger.LogInformation("[{Session}] {Name} equips {Item} in slot {EquipSlot}{Swap}", session.Id, character.Name,
            bagItem.ItemId, equipSlot, worn is null ? "" : $" (took off {worn.ItemId} into bag slot {bagSlot})");
        return ValueTask.CompletedTask;
    }

    /// <summary>Handles taking off a worn piece into the bag, the wanted slot if it fits and otherwise a free one.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the equipment slot and the wanted bag slot.</param>
    internal ValueTask OnUnequip(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (CharacterOf(session) is not { } character)
            return ValueTask.CompletedTask;
        if (marriages.InCeremony(session.Account!.LoginId))
        {
            session.Send(MarriageMessages.Result(MarriageMessages.NoEquipChange));
            return ValueTask.CompletedTask;
        }
        var request = UnequipRequest.Parse(body.Span);
        var worn = character.Equipment.Find(e => e.Slot == request.EquipSlot);
        if (worn is null)
        {
            logger.LogWarning("[{Session}] unequip slot {Slot}: nothing worn", session.Id, request.EquipSlot);
            return ValueTask.CompletedTask;
        }

        var bagSlot = Bag.Fits(character, items, worn.ItemId, request.BagSlot) ? request.BagSlot : Bag.FreeSlot(character, items, worn.ItemId);
        if (bagSlot == 0)
        {
            logger.LogInformation("[{Session}] unequip slot {Slot}: bag full", session.Id, request.EquipSlot);
            return ValueTask.CompletedTask;
        }

        character.Equipment.Remove(worn);
        character.Bag.Add(
            new BagItem
            {
                Slot = bagSlot,
                ItemId = worn.ItemId,
                Wear = worn.Wear,
                Insert = worn.Insert,
                Fusion = worn.Fusion,
                Pose = worn.Pose,
                BoxItem = worn.BoxItem
            });
        combat.Send(session);
        session.Send(ItemMessages.UnequipToBag(request.EquipSlot, bagSlot));
        world.Broadcast(session, ItemMessages.PlayerEquipChange(session.Account!.LoginId, worn.ItemId, on: false), sameMapOnly: false);
        logger.LogInformation("[{Session}] {Name} takes off {Item} into bag slot {Bag}", session.Id, character.Name, worn.ItemId, bagSlot);
        return ValueTask.CompletedTask;
    }

    /// <summary>Handles destroying a worn piece.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the equipment slot.</param>
    internal ValueTask OnDiscardEquipped(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (CharacterOf(session) is not { } character || body.Length < 1)
            return ValueTask.CompletedTask;
        var slot = body.Span[0];
        if (character.Equipment.Find(e => e.Slot == slot) is not { } worn)
            return ValueTask.CompletedTask;
        character.Equipment.Remove(worn);
        combat.Send(session);
        session.Send(ItemMessages.EquipSlotCleared(slot));
        world.Broadcast(session, ItemMessages.PlayerEquipChange(session.Account!.LoginId, worn.ItemId, on: false), sameMapOnly: false);
        logger.LogInformation("[{Session}] {Name} destroys worn {Item} ({ItemName})", session.Id, character.Name, worn.ItemId, items[worn.ItemId]?.Name);
        return ValueTask.CompletedTask;
    }

    /// <summary>Handles putting an item from a bag slot on a pet; a piece the pet wore in that slot goes back to the bag slot.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the pet slot and the bag slot.</param>
    internal ValueTask OnPetEquip(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (CharacterOf(session) is not { } character || body.Length < 2)
            return ValueTask.CompletedTask;
        var (petSlot, bagSlot) = (body.Span[0], body.Span[1]);
        var pet = character.Pets.Find(p => p.Slot == petSlot);
        var stack = character.Bag.Find(b => b.Slot == bagSlot);
        var item = stack is null ? null : items[stack.ItemId];
        if (pet is null || stack is null || item is null)
        {
            logger.LogWarning("[{Session}] pet equip: pet slot {Pet}, bag slot {Bag} refused", session.Id, petSlot, bagSlot);
            return ValueTask.CompletedTask;
        }
        var old = pet.Equipment.Find(e => e.Slot == item.EquipSlot);
        var refusal = BagLocks.LockedBy(session, bagSlot) ?? PetWearRefusal(session, item, pet)
            ?? (old is not null && !FitsInPlace(character, stack, old.ItemId) ? $"the worn {old.ItemId} does not fit back into the bag slot" : null);
        if (refusal is not null)
        {
            logger.LogWarning("[{Session}] pet equip: pet {Pet} (slot {PetSlot}), {Item} from bag slot {Bag} refused: {Why}", session.Id,
                pet.Name, petSlot, item.Id, bagSlot, refusal);
            return ValueTask.CompletedTask;
        }
        TakeFromBag(character, stack, 1);
        if (old is not null)
        {
            pet.Equipment.Remove(old);
            character.Bag.Add(
                new BagItem
                {
                    Slot = bagSlot,
                    ItemId = old.ItemId,
                    Wear = old.Wear,
                    Insert = old.Insert,
                    Fusion = old.Fusion,
                    Pose = old.Pose,
                    BoxItem = old.BoxItem
                });
        }
        pet.Equipment.Add(
            new EquippedItem { Slot = item.EquipSlot, ItemId = item.Id, Wear = stack.Wear, Insert = stack.Insert, Fusion = stack.Fusion, Pose = stack.Pose });
        combat.SendPetBonuses(session, pet);
        session.Send(PetMessages.EquipFromBag(petSlot, bagSlot));
        world.ShowPetItem(session, petSlot, item.EquipSlot);
        if (session.RidingPetSlot == petSlot)
            combat.Send(session);
        logger.LogInformation("[{Session}] {Name} dresses pet {Pet} in {Item}", session.Id, character.Name, pet.Name, item.Name);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Handles taking a piece off a pet into the bag, the wanted slot if it fits and otherwise a free one, unless the pet's gear is locked (see
    /// <see cref="GearLocked"/>) or the pet is a soldier (mercenary), as the client's petUnequipRefusal.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the pet slot, the equipment slot and the wanted bag slot.</param>
    internal ValueTask OnPetUnequip(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (CharacterOf(session) is not { } character || body.Length < 3)
            return ValueTask.CompletedTask;
        var (petSlot, equipSlot, wanted) = (body.Span[0], body.Span[1], body.Span[2]);
        if (character.Pets.Find(p => p.Slot == petSlot) is not { } pet || pet.Equipment.Find(e => e.Slot == equipSlot) is not { } worn)
            return ValueTask.CompletedTask;
        if ((GearLocked(session, pet) ?? (IsMercenary(pet) ? SoldierGear : null)) is { } locked)
        {
            logger.LogWarning("[{Session}] pet unequip: pet {Pet} (slot {PetSlot}) refused: {Why}", session.Id, pet.Name, petSlot, locked);
            return ValueTask.CompletedTask;
        }
        var bagSlot = Bag.Fits(character, items, worn.ItemId, wanted) ? wanted : Bag.FreeSlot(character, items, worn.ItemId);
        if (bagSlot == 0)
            return ValueTask.CompletedTask;
        pet.Equipment.Remove(worn);
        character.Bag.Add(
            new BagItem
            {
                Slot = bagSlot,
                ItemId = worn.ItemId,
                Wear = worn.Wear,
                Insert = worn.Insert,
                Fusion = worn.Fusion,
                Pose = worn.Pose,
                BoxItem = worn.BoxItem
            });
        combat.SendPetBonuses(session, pet);
        session.Send(PetMessages.UnequipToBag(petSlot, equipSlot, bagSlot));
        world.ShowPetItem(session, petSlot, equipSlot);
        if (session.RidingPetSlot == petSlot)
            combat.Send(session);
        logger.LogInformation("[{Session}] {Name} takes {Item} off pet {Pet}", session.Id, character.Name, worn.ItemId, pet.Name);
        return ValueTask.CompletedTask;
    }

    /// <summary>Handles destroying a piece a pet wears, unless the pet's gear is locked (see <see cref="GearLocked"/>).</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the pet slot and the equipment slot.</param>
    internal ValueTask OnPetDiscardEquipped(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (CharacterOf(session) is not { } character || body.Length < 2)
            return ValueTask.CompletedTask;
        var (petSlot, equipSlot) = (body.Span[0], body.Span[1]);
        if (character.Pets.Find(p => p.Slot == petSlot) is not { } pet || pet.Equipment.Find(e => e.Slot == equipSlot) is not { } worn)
            return ValueTask.CompletedTask;
        if (GearLocked(session, pet) is { } locked)
        {
            logger.LogWarning("[{Session}] pet discard equipped: pet {Pet} (slot {PetSlot}) refused: {Why}", session.Id, pet.Name, petSlot, locked);
            return ValueTask.CompletedTask;
        }
        pet.Equipment.Remove(worn);
        combat.SendPetBonuses(session, pet);
        session.Send(PetMessages.EquipCleared(petSlot, equipSlot));
        world.ShowPetItem(session, petSlot, equipSlot);
        if (session.RidingPetSlot == petSlot)
            combat.Send(session);
        logger.LogInformation("[{Session}] {Name} destroys {Item} worn by pet {Pet}", session.Id, character.Name, worn.ItemId, pet.Name);
        return ValueTask.CompletedTask;
    }

    /// <summary>The element id of Wind, the only element some gear asks for.</summary>
    private const byte WindElement = 4;

    /// <summary>The item sub type of pet career gear, which a career pet may wear when it was made for the pet it grew from.</summary>
    private const byte CareerGearSubType = 149;

    /// <summary>The equipment slot of the plasma weapons of the Elin pets.</summary>
    private const byte ElinWeaponSlot = 3;

    /// <summary>The NPC ids of the Elin pet and the reborn Elin pet, which wear only their own plasma weapons.</summary>
    private const ushort ElinPet = 14230, RebornElinPet = 31029; // 14230 Elin, 31029 Elin (reborn)

    /// <summary>The plasma weapons of the Elin pet and of the reborn Elin pet.</summary>
    private static readonly ushort[] ElinWeapons = [20044, 20045], RebornElinWeapons = [20048]; // 20044 Plasma Gun, 20045 and 20048 Plasma Wafer

    /// <summary>The saddles, which pets of every direction kind but 0 may wear whoever they are for (the client's IsGetSaddleKind).</summary>
    // 25208 Swift, 25214 Comfortable, 25535 Bash, 25536 Magic, 25539 Defence, 25625 Spirit, 25540 Decelerate, 25641 Tenacity
    private static readonly ushort[] Saddles = [25208, 25214, 25535, 25536, 25539, 25625, 25540, 25641];

    /// <summary>The career pets by NPC id, with the NPC id of the pet each grew from.</summary>
    private static readonly Dictionary<ushort, ushort> CareerBases = new()
    {
        [31027] = 14156, // Xaolan
        [31026] = 14081, // Niss
        [31025] = 14162, // Roca
        [31028] = 14175, // Clive
        [31035] = 14615, // Qlaya
        [31037] = 12068, // Magellan
        [31034] = 14609, // Shasha
        [31029] = 14230, // Elin
        [31040] = 15689, // Hayate
        [31033] = 14548, // Kanako
        [31038] = 12095, // Frederico
        [31039] = 15657, // Victoria
        [31032] = 14274, // Sam
    };

    /// <summary>
    /// Whether the player's body type and element let them wear an item and the item is not pet gear (the client's canWear): usable by 1 body types
    /// 1 and 3, 2 body types 2 and 4, 4 to 7 body type 1 to 4, 12 the Wind element, 8, 9 and 13 pets only, anything else anyone.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="character">The player's character.</param>
    /// <returns>True when the player may wear it.</returns>
    private static bool CanWear(NorthIsland.GameData.Items.ItemRecord item, Character character) =>
        item.PetNpcId == 0 && item.UsableBy switch
        {
            1 => character.BodyType is 1 or 3,
            2 => character.BodyType is 2 or 4,
            >= 4 and <= 7 => character.BodyType == item.UsableBy - 3, // 4 to 7: body type 1 to 4
            12 => character.Element == WindElement,
            8 or 9 or 13 => false,
            _ => true
        };

    /// <summary>
    /// Tells why a pet may not wear an item (the client's petEquipRefusal): its level, a soldier (mercenary) pet, who the item is for (gear usable
    /// by 9 or 10 fits pets of direction kind 0, by 11 or 13 pets of direction kind 1, and a saddle any pet whose direction kind is not 0), the Elin
    /// pets' own weapons, a pet up for sale in the stall or offered in a pet trade, a quest pet, a piece that is not equipment or one marked not
    /// for pets. As in the client, a soldier pet may still put on gear made for its own NPC.
    /// </summary>
    /// <param name="session">The owner's session.</param>
    /// <param name="item">The item.</param>
    /// <param name="pet">The pet.</param>
    /// <returns>The reason for the log, or null when the pet may wear it.</returns>
    private string? PetWearRefusal(GameSession session, NorthIsland.GameData.Items.ItemRecord item, Pet pet)
    {
        if (item.PetNpcId == 0)
        {
            if (item.RequiredLevel > pet.Level)
                return $"needs level {item.RequiredLevel}, the pet is level {pet.Level}";
            if (IsMercenary(pet))
                return SoldierGear;
            var directionKind = npcs[pet.NpcId]?.DirectionKind ?? 0; // an unknown NPC counts as direction kind 0, as the client's empty record
            var fits = item.UsableBy switch
            {
                0 or 8 => true, // any pet
                9 or 10 => directionKind == 0, // pets of direction kind 0
                11 or 13 => directionKind == 1, // pets of direction kind 1
                12 => pet.Element == WindElement,
                _ => false // not for pets
            } || (Saddles.Contains(item.Id) && directionKind != 0);
            if (!fits)
                return $"usable by {item.UsableBy}";
        }
        else if (item.PetNpcId != pet.NpcId
                 && !(item.SubType == CareerGearSubType && CareerBases.TryGetValue(pet.NpcId, out var grewFrom) && grewFrom == item.PetNpcId
                      && npcs[grewFrom] is { GrowKey: not 0 }))
            return $"made for pet NPC {item.PetNpcId}";
        if (item.EquipSlot == ElinWeaponSlot
            && (pet.NpcId == ElinPet && !ElinWeapons.Contains(item.Id) || pet.NpcId == RebornElinPet && !RebornElinWeapons.Contains(item.Id)))
            return "the Elin pet wears only its plasma weapons";
        if (GearLocked(session, pet) is { } locked)
            return locked;
        if (!item.IsEquipment || pet.Tradable != 1)
            return pet.Tradable != 1 ? "a quest pet" : "not equipment";
        return (item.Flags & NorthIsland.GameData.Items.ItemRecord.NotForPetsFlag) != 0 ? "not for pets" : null;
    }

    /// <summary>The refusal for the gear of a soldier (mercenary) pet, the client's own text.</summary>
    private const string SoldierGear = "Soldiers equipment can't be changed";

    /// <summary>Whether a pet is a soldier (mercenary), whose gear the client never lets change.</summary>
    /// <param name="pet">The pet.</param>
    /// <returns>True when the pet's NPC record is a mercenary.</returns>
    private bool IsMercenary(Pet pet) => npcs[pet.NpcId] is { IsMercenary: true };

    /// <summary>
    /// Tells why a pet's gear may not change: the pet is up for sale in the owner's pet stall, or the owner offers it in an open pet trade, whose
    /// partner was shown the pet with the gear it wore then (the trade keeps only the pet, so it cannot see gear taken off after the offer).
    /// </summary>
    /// <param name="session">The owner's session.</param>
    /// <param name="pet">The pet.</param>
    /// <returns>The reason for the log, or null when the gear may change.</returns>
    private static string? GearLocked(GameSession session, Pet pet) =>
        session.Stalls.Pets?.Prices.ContainsKey(pet.Slot) == true ? "the pet is in the pet stall"
        : session.Trades.Pets is { Closed: false } trade && ReferenceEquals(trade.Of(session).Pet, pet) ? "the pet is offered in a pet trade"
        : null;

    /// <summary>Whether a worn piece can take the place of the bag stack it is swapped with: the stack leaves its slot and the piece fits.</summary>
    /// <param name="character">The player's character.</param>
    /// <param name="stack">The bag stack being put on.</param>
    /// <param name="wornItemId">The item id of the worn piece that goes back to the bag.</param>
    /// <returns>True when the piece fits in the stack's slot once one item of the stack is taken.</returns>
    private bool FitsInPlace(Character character, BagItem stack, ushort wornItemId) =>
        stack.Count == 1 && Bag.Fits(character, items, wornItemId, stack.Slot, ignore: stack);

    /// <summary>
    /// Handles moving an item between bag slots: pours a potion into a bottle, moves or splits a stack into an empty slot, tops up a stack of the
    /// same stackable item with what fits, or swaps a whole stack with a different item of the same size. A slot committed elsewhere (see
    /// <see cref="BagLocks"/>) and the puppet's item stay where they are. The answer, 23/10, carries the count that moved; the client swaps when
    /// the target holds a different item.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the source slot, the target slot and the count (0 for the whole stack).</param>
    internal ValueTask OnMove(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (CharacterOf(session) is not { } character)
            return ValueTask.CompletedTask;
        var move = BagMoveRequest.Parse(body.Span);
        var source = character.Bag.Find(b => b.Slot == move.From);
        if (source is null || move.To is < BagItem.FirstSlot or > BagItem.LastSlot || move.From == move.To)
        {
            logger.LogWarning("[{Session}] bad bag move {From} -> {To} x{Count}", session.Id, move.From, move.To, move.Count);
            return ValueTask.CompletedTask;
        }

        if ((BagLocks.LockedBy(session, move.From) ?? BagLocks.LockedBy(session, move.To)
                ?? (HoldsPuppet(character, move.From) || HoldsPuppet(character, move.To) ? "the puppet's item" : null)) is { } locked)
        {
            logger.LogWarning("[{Session}] bag move {From} -> {To}: a slot holds {Why}, it stays put", session.Id, move.From, move.To, locked);
            return ValueTask.CompletedTask;
        }

        var count = move.Count == 0 || move.Count > source.Count ? source.Count : move.Count;
        var target = character.Bag.Find(b => b.Slot == move.To);
        if (target is not null && bottles.TryFill(session, character, source, target))
            return ValueTask.CompletedTask;
        if (target is null && !Bag.Fits(character, items, source.ItemId, move.To, ignore: count == source.Count ? source : null))
        {
            logger.LogWarning("[{Session}] bag move {From} -> {To}: item {Item} does not fit there", session.Id, move.From, move.To, source.ItemId);
            return ValueTask.CompletedTask;
        }
        if (target is null)
        {
            if (count == source.Count)
                source.Slot = move.To;
            else
            {
                source.Count -= count;
                character.Bag.Add(
                    new BagItem
                    {
                        Slot = move.To,
                        ItemId = source.ItemId,
                        Count = count,
                        Wear = source.Wear,
                        Insert = source.Insert,
                        Fusion = source.Fusion,
                        Pose = source.Pose,
                        BoxItem = source.BoxItem,
                        Used = source.Used,
                        Extra = source.Extra
                    });
            }
        }
        else if (target.ItemId == source.ItemId && Bag.IsStackable(items[source.ItemId]))
        {
            count = (byte)Math.Clamp(Bag.MaxStack - target.Count, 0, count);
            if (count == 0)
                return ValueTask.CompletedTask;
            target.Count += count;
            TakeFromBag(character, source, count);
        }
        else if (target.ItemId != source.ItemId && count == source.Count && Bag.SameSize(items, source.ItemId, target.ItemId))
        {
            (source.Slot, target.Slot) = (target.Slot, source.Slot);
        }
        else
        {
            logger.LogWarning("[{Session}] bag move {From} -> {To} x{Count}: {Item} cannot go onto {Other}", session.Id, move.From, move.To, count,
                source.ItemId, target.ItemId);
            return ValueTask.CompletedTask;
        }

        session.Send(ItemMessages.BagMove(move.From, count, move.To));
        return ValueTask.CompletedTask;
    }

    /// <summary>Whether a bag slot holds the item of the running puppet play, which must not move.</summary>
    /// <param name="character">The player's character.</param>
    /// <param name="slot">The bag slot.</param>
    /// <returns>True when the slot holds the puppet's item.</returns>
    private static bool HoldsPuppet(Character character, byte slot) => character.PuppetBagSlot != 0 && character.PuppetBagSlot == slot;

    /// <summary>The farthest the player may stand from a thing on the ground on each axis to pick it up, in pixels (the client's limit).</summary>
    private const int PickUpReach = 170;

    /// <summary>
    /// Handles picking up a thing from the ground within reach, outside battle and alive: gold goes to the purse, an item to the bag when all of it
    /// fits, with the attributes it was dropped with. A thing that cannot be transferred stays for the player who dropped it.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the index of the thing on the ground, 16 bits.</param>
    internal ValueTask OnPickUp(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (CharacterOf(session) is not { } character || body.Length < 2)
            return ValueTask.CompletedTask;
        var index = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(body.Span);
        if (session.Battle is not null || character.Hp <= 0)
        {
            logger.LogWarning("[{Session}] pick up ground index {Index}: {Why}", session.Id, index,
                session.Battle is not null ? "in a battle" : "dead");
            return ValueTask.CompletedTask;
        }
        var answer = false; // whether the client is told it cannot pick the thing up
        var thing = ground.TakeIf(session, index, (seen, x, y) =>
        {
            if (Math.Abs(x - character.X) > PickUpReach || Math.Abs(y - character.Y) > PickUpReach)
                return $"too far away at ({x}, {y}), the player is at ({character.X}, {character.Y})";
            answer = true;
            if (seen.Owner != 0 && seen.Owner != session.Account!.LoginId)
                return $"cannot be transferred and belongs to {seen.Owner}";
            if (seen.Gold == 0 && Bag.Add(Bag.Trial(character.Bag), items, seen.ItemId, seen.Count).Sum(c => c.Added) < seen.Count)
                return "no room in the bag";
            answer = false;
            return null;
        }, out var refused);
        if (thing is null)
        {
            if (refused is null)
                logger.LogInformation("[{Session}] pick up: nothing at ground index {Index}", session.Id, index);
            else if (answer)
                logger.LogInformation("[{Session}] pick up ground index {Index}: {Why}", session.Id, index, refused);
            else
                logger.LogWarning("[{Session}] pick up ground index {Index}: {Why}", session.Id, index, refused);
            if (answer)
                session.Send(new Protocol.IO.PacketWriter(Action23.Code, Action23.CannotPickUp).ToArray());
            return ValueTask.CompletedTask;
        }

        if (thing.Gold > 0)
        {
            Purse.Credit(session, character, thing.Gold);
            logger.LogInformation("[{Session}] {Name} picks up {Gold} gold ({Total} now)", session.Id, character.Name, thing.Gold, character.Gold);
            return ValueTask.CompletedTask;
        }
        var added = Bag.Add(character, items, thing.ItemId, thing.Count, thing.Wear, thing.Insert, thing.Fusion, thing.Pose, thing.BoxItem,
            thing.Used, thing.Extra);
        if (thing.HasAttributes)
            Bag.SendAdded(session, added); // 23/8 names each slot with the stack's attributes, which 23/6 cannot carry
        else
            Bag.SendObtained(session, items, thing.ItemId, added);
        logger.LogInformation("[{Session}] {Name} picks up {Item} ({ItemName}) x{Count} from ground index {Index}", session.Id, character.Name,
            thing.ItemId, items[thing.ItemId]?.Name, thing.Count, index);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Handles dropping bag items on the ground. Low-level players are asked to confirm first, depending on the configured level.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the bag slot and the count (0 for the whole stack).</param>
    internal ValueTask OnDrop(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (CharacterOf(session) is not { } character || body.Length < 2)
            return ValueTask.CompletedTask;
        if (options.Value.DropConfirmBelowLevel > 0 && character.Level < options.Value.DropConfirmBelowLevel
            && character.Bag.Find(b => b.Slot == body.Span[0]) is { } asked && !BagLocks.IsLocked(session, asked.Slot))
        {
            var count = body.Span[1] == 0 || body.Span[1] > asked.Count ? asked.Count : body.Span[1];
            session.Send(new PacketWriter(Action23.Code, Action23.DiscardConfirm).U8(1).U8(asked.Slot).U16(asked.ItemId).U8((byte)count).ToArray());
            return ValueTask.CompletedTask;
        }
        var noTrade = character.Bag.Find(b => b.Slot == body.Span[0]) is { } dropped && Bag.IsNoTrade(items, dropped);
        if (TakeStack(session, character, body.Span[0], body.Span[1], "drop") is not { } dropping)
            return ValueTask.CompletedTask;
        var taken = noTrade ? dropping with { Owner = session.Account!.LoginId } : dropping;
        if (!ground.Drop(session, taken))
        {
            logger.LogWarning("[{Session}] drop: the ground of map {Map} is full, {Item} lost", session.Id, character.MapId, taken.ItemId);
            return ValueTask.CompletedTask;
        }
        logger.LogInformation("[{Session}] {Name} drops {Item} ({ItemName}) x{Count} at ({X}, {Y})", session.Id, character.Name,
            taken.ItemId, items[taken.ItemId]?.Name, taken.Count, character.X, character.Y);
        return ValueTask.CompletedTask;
    }

    /// <summary>Handles destroying bag items.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the bag slot and the count (0 for the whole stack).</param>
    internal ValueTask OnDiscard(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (CharacterOf(session) is not { } character || body.Length < 2)
            return ValueTask.CompletedTask;
        if (TakeStack(session, character, body.Span[0], body.Span[1], "discard") is { } taken)
            logger.LogInformation("[{Session}] {Name} destroys {Item} ({ItemName}) x{Count}", session.Id, character.Name,
                taken.ItemId, items[taken.ItemId]?.Name, taken.Count);
        return ValueTask.CompletedTask;
    }

    /// <summary>Handles throwing gold on the ground.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the amount of gold, 32 bits.</param>
    internal ValueTask OnThrowMoney(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (CharacterOf(session) is not { } character || body.Length < 4)
            return ValueTask.CompletedTask;
        var amount = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(body.Span);
        if (amount <= 0 || amount > character.Gold)
        {
            logger.LogWarning("[{Session}] {Name} cannot throw {Amount} gold (has {Gold})", session.Id, character.Name, amount, character.Gold);
            return ValueTask.CompletedTask;
        }
        if (!ground.Drop(session, new GroundThing(0, 0, amount)))
            return ValueTask.CompletedTask;
        character.Gold -= amount;
        session.Send(PlayerMessages.MoneyChange(-amount));
        logger.LogInformation("[{Session}] {Name} throws {Amount} gold at ({X}, {Y}) ({Gold} left)", session.Id, character.Name,
            amount, character.X, character.Y, character.Gold);
        return ValueTask.CompletedTask;
    }

    /// <summary>Handles using an item as a supply: the same as <see cref="OnUse"/> without the drinking sound.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the bag slot, the count and the pet slot (0 for the player).</param>
    internal ValueTask OnSupply(GameSession session, ReadOnlyMemory<byte> body) => Use(session, body, quiet: true);

    /// <summary>Handles using an item from the bag on the player or on a pet.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the bag slot, the count and the pet slot (0 for the player).</param>
    internal ValueTask OnUse(GameSession session, ReadOnlyMemory<byte> body) => Use(session, body, quiet: false);

    /// <summary>
    /// Uses an item: the item effect handlers get it first, then a pet eats it, a tent is pitched, or its HP and SP restores are applied to the player.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the bag slot, the count and the pet slot (0 for the player).</param>
    /// <param name="quiet">True to leave out the drinking sound.</param>
    private ValueTask Use(GameSession session, ReadOnlyMemory<byte> body, bool quiet)
    {
        if (CharacterOf(session) is not { } character || body.Length < 3 || session.Battle is not null)
            return ValueTask.CompletedTask;
        var (slot, count, petSlot) = (body.Span[0], body.Span[1], body.Span[2]);
        var stack = character.Bag.Find(b => b.Slot == slot);
        if (stack is not null && BagLocks.LockedBy(session, slot) is { } locked)
        {
            logger.LogWarning("[{Session}] use item: {Item} in bag slot {Slot} is {Why}", session.Id, stack.ItemId, slot, locked);
            return ValueTask.CompletedTask;
        }
        var item = stack is null ? null : items[stack.ItemId];
        if (stack?.ItemId == ItemEffectHandlers.Stopwatch)
        {
            // A client without the Stopwatch's own packet (23/110) sends a plain use: pause the double exp, or let it go on when paused.
            return effects.OnStopwatch(session, new[] { character.ExpPausedSince is null ? stack.Slot : (byte)0 });
        }
        if (stack is not null && item is not null && GiftBoxContent(item) is var content and not 0)
        {
            OpenGiftBox(session, character, stack, item, content);
            return ValueTask.CompletedTask;
        }
        if (stack is not null && item is not null && effects.TryUse(session, character, stack, item, petSlot))
            return ValueTask.CompletedTask;
        var pet = petSlot == 0 ? null : character.Pets.Find(p => p.Slot == petSlot);
        if (stack is null || item is null || petSlot != 0 && pet is null)
        {
            logger.LogWarning("[{Session}] use item: bag slot {Slot} (pet {Pet}) cannot be used", session.Id, slot, petSlot);
            return ValueTask.CompletedTask;
        }
        if (pet is not null)
        {
            FeedPet(session, character, stack, item, pet, count, quiet);
            return ValueTask.CompletedTask;
        }
        if (item.Type == TentHandlers.TentItemType)
        {
            tents.Pitch(session, stack);
            return ValueTask.CompletedTask;
        }
        if (item.Restores.Count == 0)
        {
            logger.LogWarning("[{Session}] {Name} uses {Item} ({ItemName}): no known effect", session.Id, character.Name, item.Id, item.Name);
            return ValueTask.CompletedTask;
        }

        var uses = (byte)Math.Clamp((int)count, 1, stack.Count);
        var (hpBefore, spBefore) = (character.Hp, character.Sp);
        for (var i = 0; i < uses; i++)
        {
            foreach (var (attr, amount) in item.Restores)
            {
                if (attr == Action8.AttrHp)
                    character.Hp = Math.Min(character.MaxHp, character.Hp + amount);
                else
                    character.Sp = Math.Min(character.MaxSp, character.Sp + amount);
            }
        }
        TakeFromBag(character, stack, uses);
        session.Send(ItemMessages.BagSlotDecrease(slot, uses));
        if (character.Hp != hpBefore)
            session.Send(InnMessages.SetAttribute(Action8.AttrHp, character.Hp));
        if (character.Sp != spBefore)
            session.Send(InnMessages.SetAttribute(Action8.AttrSp, character.Sp));
        if (!quiet)
            session.Send(ItemMessages.UsedSound());
        logger.LogInformation("[{Session}] {Name} uses {Item} ({ItemName}) x{Count}: HP {Hp}/{MaxHp}, SP {Sp}/{MaxSp}", session.Id,
            character.Name, item.Id, item.Name, uses, character.Hp, character.MaxHp, character.Sp, character.MaxSp);
        return ValueTask.CompletedTask;
    }

    /// <summary>The item type of gift boxes, which hold the item their alias names (the client's GetItemGiftBoxId).</summary>
    private const byte GiftBoxType = 17;

    /// <summary>Gets the item a gift box holds.</summary>
    /// <param name="item">The item used.</param>
    /// <returns>The item id inside, or 0 when the item is not a gift box.</returns>
    private static ushort GiftBoxContent(NorthIsland.GameData.Items.ItemRecord item) =>
        item.Type == GiftBoxType && item.Words429.Length > 1 ? item.Words429[1] : (ushort)0;

    /// <summary>Opens a gift box: takes one box and puts the item it holds in the bag, when it fits.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's character.</param>
    /// <param name="stack">The box's bag stack.</param>
    /// <param name="box">The box's item record.</param>
    /// <param name="content">The item inside.</param>
    private void OpenGiftBox(GameSession session, Character character, BagItem stack, NorthIsland.GameData.Items.ItemRecord box, ushort content)
    {
        List<BagItem> trial = [
            .. character.Bag.Where(b => b != stack || b.Count > 1).Select(
                b => new BagItem
                {
                    Slot = b.Slot, ItemId = b.ItemId, Count = b == stack ? (byte)(b.Count - 1) : b.Count, Wear = b.Wear, Fusion = b.Fusion, Pose = b.Pose,
                    BoxItem = b.BoxItem
                })
        ];
        if (items[content] is null || Bag.Add(trial, items, content, 1).Sum(c => c.Added) != 1)
        {
            logger.LogInformation("[{Session}] {Name} opens {Box}: no room for item {Item}", session.Id, character.Name, box.Name, content);
            session.Send(new PacketWriter(Action23.Code, Action23.CannotPickUp).ToArray());
            return;
        }
        TakeFromBag(character, stack, 1);
        session.Send(ItemMessages.BagSlotDecrease(stack.Slot, 1));
        Bag.SendObtained(session, items, content, Bag.Add(character, items, content, 1));
        logger.LogInformation("[{Session}] {Name} opens {Box} and gets {Item}", session.Id, character.Name, box.Name, items[content]?.Name);
    }

    /// <summary>Feeds a pet items that restore HP or SP, unless the pet is a quest NPC.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's character.</param>
    /// <param name="stack">The bag stack that was used.</param>
    /// <param name="item">The definition of the item.</param>
    /// <param name="pet">The pet that eats.</param>
    /// <param name="count">How many to use; at least 1 and at most the stack.</param>
    /// <param name="quiet">True to leave out the drinking sound.</param>
    private void FeedPet(
        GameSession session,
        Character character,
        BagItem stack,
        NorthIsland.GameData.Items.ItemRecord item,
        Pet pet,
        byte count,
        bool quiet)
    {
        if (item.Restores.Count == 0 || pet.Tradable == 0)
        {
            logger.LogWarning("[{Session}] {Name} gives {Item} ({ItemName}) to pet {Pet}: {Why}", session.Id, character.Name, item.Id, item.Name, pet.Name,
                pet.Tradable == 0 ? "a quest NPC" : "no known effect");
            return;
        }
        var uses = (byte)Math.Clamp((int)count, 1, stack.Count);
        var (hpBefore, spBefore) = (pet.Hp, pet.Sp);
        for (var i = 0; i < uses; i++)
        {
            foreach (var (attr, amount) in item.Restores)
            {
                if (attr == Action8.AttrHp)
                    pet.Hp = Math.Min(pet.MaxHp, pet.Hp + amount);
                else
                    pet.Sp = Math.Min(pet.MaxSp, pet.Sp + amount);
            }
        }
        TakeFromBag(character, stack, uses);
        session.Send(ItemMessages.BagSlotDecrease(stack.Slot, uses));
        if (pet.Hp != hpBefore)
            session.Send(PetMessages.SetAttribute(pet.Slot, Action8.AttrHp, pet.Hp));
        if (pet.Sp != spBefore)
            session.Send(PetMessages.SetAttribute(pet.Slot, Action8.AttrSp, pet.Sp));
        if (!quiet)
            session.Send(ItemMessages.UsedSound());
        logger.LogInformation("[{Session}] {Name} feeds {Item} ({ItemName}) x{Count} to pet {Pet}: HP {Hp}/{MaxHp}, SP {Sp}/{MaxSp}", session.Id,
            character.Name, item.Id, item.Name, uses, pet.Name, pet.Hp, pet.MaxHp, pet.Sp, pet.MaxSp);
    }

    /// <summary>Takes items out of a bag slot to drop or destroy them, and tells the client.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's character.</param>
    /// <param name="slot">The bag slot.</param>
    /// <param name="count">How many to take; 0 or more than the stack means the whole stack.</param>
    /// <param name="what">What is being done, for the log.</param>
    /// <returns>
    /// What was taken, with every attribute of the stack, or null when the slot is empty or committed elsewhere (see <see cref="BagLocks"/>).
    /// </returns>
    private GroundThing? TakeStack(
        GameSession session,
        Character character,
        byte slot,
        byte count,
        string what)
    {
        var stack = character.Bag.Find(b => b.Slot == slot);
        var refusal = stack is null ? "empty" : BagLocks.LockedBy(session, slot);
        if (stack is null || refusal is not null)
        {
            logger.LogWarning("[{Session}] {What}: bag slot {Slot} cannot be used ({Why})", session.Id, what, slot, refusal);
            return null;
        }
        var taken = count == 0 || count > stack.Count ? stack.Count : count;
        TakeFromBag(character, stack, taken);
        session.Send(ItemMessages.BagSlotDecrease(slot, taken));
        return new GroundThing(stack.ItemId, taken, 0, stack.Wear, Insert: stack.Insert, Fusion: stack.Fusion, Pose: stack.Pose,
            BoxItem: stack.BoxItem, Used: stack.Used, Extra: stack.Extra);
    }

    /// <summary>Adds one item to the bag.</summary>
    /// <param name="character">The player's character.</param>
    /// <param name="itemId">The item id.</param>
    private void AddToBag(Character character, ushort itemId) => Bag.Add(character, items, itemId, 1);

    /// <summary>Takes a number of items out of a stack, removing the stack when it is empty.</summary>
    /// <param name="character">The player's character.</param>
    /// <param name="item">The stack.</param>
    /// <param name="count">How many to take, at most the stack.</param>
    private static void TakeFromBag(Character character, BagItem item, byte count)
    {
        item.Count -= Math.Min(count, item.Count);
        if (item.Count == 0)
            character.Bag.Remove(item);
    }

    /// <summary>The character of a session that is in the world.</summary>
    /// <param name="session">The player's session.</param>
    /// <returns>The character, or null when the session is not in the world.</returns>
    private static Character? CharacterOf(GameSession session)
    {
        var character = session.ActiveCharacter;
        return session.State == SessionState.InWorld && character is not null && session.Account is not null ? character : null;
    }
}
