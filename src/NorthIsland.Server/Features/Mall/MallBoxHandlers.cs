using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NorthIsland.Protocol.Messages.Items;
using NorthIsland.Protocol.Messages.Player;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.Features.Mall;

/// <summary>Handles opening boxes, lucky bags and gift boxes from the bag, and the lucky bag content preview.</summary>
/// <param name="items">Item definitions.</param>
/// <param name="options">Mall options; names the lucky bag override file.</param>
/// <param name="logger">Logger.</param>
public sealed class MallBoxHandlers(
    ItemData items,
    IOptions<MallOptions> options,
    NpcData npcs,
    FormulaData formulas,
    ILogger<MallBoxHandlers> logger)
{
    /// <summary>Reward tables by box item id.</summary>
    private readonly IReadOnlyDictionary<ushort, IReadOnlyList<MallBoxEntry>> _tables = MallBoxes.Load(
        options.Value.MallBoxes.LuckyBagsFile is { Length: > 0 } f ? ServerPaths.Resolve(f) : null);

    /// <summary>Fixed contents by gift box item id.</summary>
    private readonly IReadOnlyDictionary<ushort, GiftBox> _gifts = GiftBoxes.Load();

    /// <summary>Item id of the Purple Chest, which needs a Purple Chest Key to open.</summary>
    public const ushort PurpleChest = 34268; // Purple Chest

    /// <summary>Item ids of the keys that open the Purple Chest.</summary>
    public static readonly ImmutableArray<ushort> PurpleKeys = [30334, 30371]; // 30334 Purple Chest Key, 30371 Purple Chest Key

    /// <summary>
    /// Answers a lucky bag preview request with the bag's reward list, or without a list when the client's count already matches the table.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal ValueTask OnPreview(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || body.Length < 3)
            return ValueTask.CompletedTask;
        var bagId = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(body.Span);
        if (!_tables.TryGetValue(bagId, out var table) || items[bagId] is not { Type: 41 })
            return ValueTask.CompletedTask;
        session.Send(LuckyBagMessages.Info(bagId, body.Span[2] == table.Count ? null : [.. table.Select(e => (e.ItemId, e.Count))]));
        return ValueTask.CompletedTask;
    }

    /// <summary>Opens a gift box in the bag: takes one box and gives its fixed items and gold, provided everything fits.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal ValueTask OnOpenGift(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || session.ActiveCharacter is not { } c || session.Battle is not null || body.Length < 1)
            return ValueTask.CompletedTask;
        var stack = c.Bag.Find(b => b.Slot == body.Span[0]);
        if (stack is null || !_gifts.TryGetValue(stack.ItemId, out var box))
        {
            logger.LogInformation("[{Session}] {Name}: bag slot {Slot} is not a gift box with known contents", session.Id, c.Name, body.Span[0]);
            return ValueTask.CompletedTask;
        }
        var trial = Bag.Trial(c.Bag);
        Bag.TakeAt(trial, stack.Slot, 1);
        if (box.Items.Any(i => items[i.Item] is null || Bag.Add(trial, items, i.Item, i.Count).Sum(x => x.Added) != i.Count)
            || (long)c.Gold + box.Gold > int.MaxValue)
        {
            logger.LogInformation("[{Session}] {Name}: no room for the contents of {Box}", session.Id, c.Name, items[stack.ItemId]!.Name.Trim());
            return ValueTask.CompletedTask;
        }
        var name = items[stack.ItemId]!.Name.Trim();
        stack.Count--;
        if (stack.Count == 0)
            c.Bag.Remove(stack);
        session.Send(ItemMessages.BagSlotDecrease(body.Span[0], 1));
        foreach (var (item, count) in box.Items)
            foreach (var (added, n) in Bag.Add(c, items, item, count))
                session.Send(ItemMessages.BagAdd(new BagEntry { Slot = added.Slot, ItemId = added.ItemId, Count = n, Wear = added.Wear }));
        if (box.Gold > 0)
        {
            c.Gold += box.Gold;
            session.Send(PlayerMessages.MoneyChange(box.Gold));
        }
        logger.LogInformation("[{Session}] {Name} opens {Box}: {Items} item stack(s), {Gold} gold", session.Id, c.Name, name, box.Items.Count, box.Gold);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Opens a box from a bag slot: checks that every reward of the box's table would fit with one box (and one key for the Purple Chest)
    /// taken out, then rolls one reward by weight, takes the box and the key and gives the reward. The room is checked for every reward
    /// before the roll, so a full bag refuses the box without a roll instead of refusing only the rewards that do not fit, which would let
    /// a player open the box again and again until a wanted reward is the one that fits.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal ValueTask OnOpen(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || session.ActiveCharacter is not { } c || session.Battle is not null || body.Length < 1)
            return ValueTask.CompletedTask;
        var stack = c.Bag.Find(b => b.Slot == body.Span[0]);
        if (stack?.ItemId == PetEgg.ItemId)
        {
            HatchPetEgg(session, c, stack);
            return ValueTask.CompletedTask;
        }
        if (stack is null || !_tables.TryGetValue(stack.ItemId, out var rows))
        {
            logger.LogInformation("[{Session}] {Name}: bag slot {Slot} does not open as a box", session.Id, c.Name, body.Span[0]);
            return ValueTask.CompletedTask;
        }
        var boxName = items[stack.ItemId]?.Name.Trim() ?? stack.ItemId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        List<MallBoxEntry> table = [.. rows.Where(e => items[e.ItemId] is not null)];
        if (table.Count == 0)
        {
            logger.LogWarning("[{Session}] {Name}: {Box} has no reward with a known item", session.Id, c.Name, boxName);
            return ValueTask.CompletedTask;
        }
        BagItem? key = null;
        if (stack.ItemId == PurpleChest)
        {
            key = body.Length >= 2 ? c.Bag.Find(b => b.Slot == body.Span[1]) : null;
            if (key is null || PurpleKeys.IndexOf(key.ItemId) < 0)
            {
                logger.LogInformation("[{Session}] {Name}: the Purple Chest needs a Purple Chest Key", session.Id, c.Name);
                return ValueTask.CompletedTask;
            }
        }
        if (table.Find(e => !Fits(c, stack, key, e)) is { } tooBig)
        {
            logger.LogInformation(
                "[{Session}] {Name}: no room for every reward of {Box} (not for {Item} x{Count}); not opened",
                session.Id,
                c.Name,
                boxName,
                items[tooBig.ItemId]!.Name.Trim(),
                tooBig.Count);
            return ValueTask.CompletedTask;
        }
        var reward = MallBoxes.Roll(table, GameRandom.Shared.Next);
        var slot = stack.Slot;
        stack.Count--;
        if (stack.Count == 0)
            c.Bag.Remove(stack);
        session.Send(ItemMessages.BagSlotDecrease(slot, 1));
        if (key is not null)
        {
            key.Count--;
            if (key.Count == 0)
                c.Bag.Remove(key);
            session.Send(ItemMessages.BagSlotDecrease(key.Slot, 1));
        }
        var added = Bag.SendObtained(session, items, reward.ItemId, Bag.Add(c, items, reward.ItemId, reward.Count));
        logger.LogInformation("[{Session}] {Name} opens {Box}: {Item} x{Count}", session.Id, c.Name, boxName, items[reward.ItemId]!.Name.Trim(), added);
        return ValueTask.CompletedTask;
    }

    /// <summary>Tells whether one reward would fit in the bag once one box, and one key when there is one, are taken out, trying it on a copy.</summary>
    /// <param name="c">The player's character.</param>
    /// <param name="box">The box's bag stack.</param>
    /// <param name="key">The key's bag stack, or null when the box needs none.</param>
    /// <param name="reward">The reward to try.</param>
    /// <returns>True when the whole count fits.</returns>
    private bool Fits(Character c, BagItem box, BagItem? key, MallBoxEntry reward)
    {
        var trial = Bag.Trial(c.Bag);
        Bag.TakeAt(trial, box.Slot, 1);
        if (key is not null)
            Bag.TakeAt(trial, key.Slot, 1);
        return Bag.Add(trial, items, reward.ItemId, reward.Count).Sum(x => x.Added) == reward.Count;
    }

    /// <summary>
    /// Hatches a Pet Egg: a random pet joins the team. The team has to have a free place, and the roll is only among the pets of the egg that
    /// the player has neither in the team nor with the pet keeper, so the egg is used up on every roll. Refusing a rolled pet the player
    /// already has, and keeping the egg, would let a player hatch the egg again and again until a wanted pet comes up.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="c">The player's character.</param>
    /// <param name="stack">The egg's bag stack.</param>
    private void HatchPetEgg(GameSession session, Character c, BagItem stack)
    {
        var npcId = c.Pets.Count >= Pet.MaxPets
            ? (ushort)0
            : PetEgg.Roll(GameRandom.Shared.Next, id => npcs[id] is not null && !c.Pets.Exists(p => p.NpcId == id) && !c.StoredPets.Exists(p => p.NpcId == id));
        if (npcId == 0 || npcs[npcId] is not { } npc)
        {
            var code = c.Pets.Count >= Pet.MaxPets ? Action83.PetSlotsFull : Action83.SamePet;
            logger.LogInformation("[{Session}] {Name}: Pet Egg refused (code {Code}: the team is full or has every pet of the egg)", session.Id, c.Name, code);
            session.Send(new NorthIsland.Protocol.IO.PacketWriter(Action83.Code, code).ToArray());
            return;
        }
        stack.Count--;
        if (stack.Count == 0)
            c.Bag.Remove(stack);
        session.Send(ItemMessages.BagSlotDecrease(stack.Slot, 1));
        var pet = Pets.Create(npc, Pets.FreeSlot(c), formulas.Table);
        c.Pets.Add(pet);
        session.Send(PetMessages.Joined(session.Account!.LoginId, Pets.Info(pet)));
        logger.LogInformation("[{Session}] {Name} hatches a Pet Egg: {Pet} ({Npc}) joins the team in slot {Slot}", session.Id, c.Name, pet.Name, pet.NpcId,
            pet.Slot);
    }
}
