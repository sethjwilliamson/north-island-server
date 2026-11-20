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
        if (record!.RequiredLevel > character.Level)
        {
            logger.LogWarning("[{Session}] {Name} (level {Level}) cannot wear {Item}, needs level {Required}",
                session.Id, character.Name, character.Level, bagItem.ItemId, record.RequiredLevel);
            return ValueTask.CompletedTask;
        }

        var worn = character.Equipment.Find(e => e.Slot == equipSlot);
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
        if (pet is null || stack is null || item is not { IsEquipment: true } || item.RequiredLevel > pet.Level)
        {
            logger.LogWarning("[{Session}] pet equip: pet slot {Pet}, bag slot {Bag} refused", session.Id, petSlot, bagSlot);
            return ValueTask.CompletedTask;
        }
        var old = pet.Equipment.Find(e => e.Slot == item.EquipSlot);
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

    /// <summary>Handles taking a piece off a pet into the bag, the wanted slot if it fits and otherwise a free one.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the pet slot, the equipment slot and the wanted bag slot.</param>
    internal ValueTask OnPetUnequip(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (CharacterOf(session) is not { } character || body.Length < 3)
            return ValueTask.CompletedTask;
        var (petSlot, equipSlot, wanted) = (body.Span[0], body.Span[1], body.Span[2]);
        if (character.Pets.Find(p => p.Slot == petSlot) is not { } pet || pet.Equipment.Find(e => e.Slot == equipSlot) is not { } worn)
            return ValueTask.CompletedTask;
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

    /// <summary>Handles destroying a piece a pet wears.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the pet slot and the equipment slot.</param>
    internal ValueTask OnPetDiscardEquipped(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (CharacterOf(session) is not { } character || body.Length < 2)
            return ValueTask.CompletedTask;
        var (petSlot, equipSlot) = (body.Span[0], body.Span[1]);
        if (character.Pets.Find(p => p.Slot == petSlot) is not { } pet || pet.Equipment.Find(e => e.Slot == equipSlot) is not { } worn)
            return ValueTask.CompletedTask;
        pet.Equipment.Remove(worn);
        combat.SendPetBonuses(session, pet);
        session.Send(PetMessages.EquipCleared(petSlot, equipSlot));
        world.ShowPetItem(session, petSlot, equipSlot);
        if (session.RidingPetSlot == petSlot)
            combat.Send(session);
        logger.LogInformation("[{Session}] {Name} destroys {Item} worn by pet {Pet}", session.Id, character.Name, worn.ItemId, pet.Name);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Handles moving an item between bag slots: pours a potion into a bottle, moves or splits a stack into an empty slot, tops up a stack of the
    /// same stackable item with what fits, or swaps a whole stack with a different item of the same size. The ridden vehicle and the puppet's
    /// item stay where they are. The answer, 23/10, carries the count that moved; the client swaps when the target holds a different item.
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

        var count = move.Count == 0 || move.Count > source.Count ? source.Count : move.Count;
        var target = character.Bag.Find(b => b.Slot == move.To);
        if (target is not null && bottles.TryFill(session, character, source, target))
            return ValueTask.CompletedTask;
        if (Pinned(session, character, move.From) || Pinned(session, character, move.To))
        {
            logger.LogWarning("[{Session}] bag move {From} -> {To}: the ridden vehicle or the puppet's item stays put", session.Id, move.From, move.To);
            return ValueTask.CompletedTask;
        }
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
            count = (byte)Math.Min(count, Bag.MaxStack - target.Count);
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

    /// <summary>Whether a bag slot holds something that must not move: the vehicle being ridden or the item lent to a puppet helper.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's character.</param>
    /// <param name="slot">The bag slot.</param>
    /// <returns>True when the slot is pinned.</returns>
    private static bool Pinned(GameSession session, Character character, byte slot) =>
        session.Vehicle?.BagSlot == slot || (character.PuppetBagSlot != 0 && character.PuppetBagSlot == slot);

    /// <summary>Handles picking up a thing from the ground: gold goes to the purse, an item to the bag when all of it fits.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the index of the thing on the ground, 16 bits.</param>
    internal ValueTask OnPickUp(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (CharacterOf(session) is not { } character || body.Length < 2)
            return ValueTask.CompletedTask;
        var index = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(body.Span);
        if (ground.Peek(session, index) is not { } peek)
        {
            logger.LogInformation("[{Session}] pick up: nothing at ground index {Index}", session.Id, index);
            return ValueTask.CompletedTask;
        }
        if (peek.Owner != 0 && peek.Owner != session.Account!.LoginId)
        {
            logger.LogInformation("[{Session}] pick up: {Item} at ground index {Index} cannot be transferred and belongs to {Owner}", session.Id,
                peek.ItemId, index, peek.Owner);
            session.Send(new Protocol.IO.PacketWriter(Action23.Code, Action23.CannotPickUp).ToArray());
            return ValueTask.CompletedTask;
        }
        if (peek.Gold == 0)
        {
            List<BagItem> trial = [
                .. character.Bag.Select(
                    b =>
                        new BagItem { Slot = b.Slot, ItemId = b.ItemId, Count = b.Count, Wear = b.Wear, Fusion = b.Fusion, Pose = b.Pose, BoxItem = b.BoxItem })
            ];
            if (Bag.Add(trial, items, peek.ItemId, peek.Count).Sum(c => c.Added) < peek.Count)
            {
                session.Send(new Protocol.IO.PacketWriter(Action23.Code, Action23.CannotPickUp).ToArray());
                return ValueTask.CompletedTask;
            }
        }
        if (ground.Take(session, index) is not { } thing)
            return ValueTask.CompletedTask;

        if (thing.Gold > 0)
        {
            Purse.Credit(session, character, thing.Gold);
            logger.LogInformation("[{Session}] {Name} picks up {Gold} gold ({Total} now)", session.Id, character.Name, thing.Gold, character.Gold);
            return ValueTask.CompletedTask;
        }
        Bag.SendObtained(session, items, thing.ItemId, Bag.Add(character, items, thing.ItemId, thing.Count, thing.Wear));
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
            && character.Bag.Find(b => b.Slot == body.Span[0]) is { } asked && session.Vehicle?.BagSlot != asked.Slot)
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
    /// <returns>What was taken, or null when the slot is empty or holds the player's vehicle.</returns>
    private GroundThing? TakeStack(
        GameSession session,
        Character character,
        byte slot,
        byte count,
        string what)
    {
        var stack = character.Bag.Find(b => b.Slot == slot);
        if (stack is null || session.Vehicle?.BagSlot == slot)
        {
            logger.LogWarning("[{Session}] {What}: bag slot {Slot} cannot be used", session.Id, what, slot);
            return null;
        }
        var taken = count == 0 || count > stack.Count ? stack.Count : count;
        TakeFromBag(character, stack, taken);
        session.Send(ItemMessages.BagSlotDecrease(slot, taken));
        return new GroundThing(stack.ItemId, taken, 0, stack.Wear);
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
