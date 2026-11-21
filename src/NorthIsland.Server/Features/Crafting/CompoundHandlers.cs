using Microsoft.Extensions.Logging;
using NorthIsland.GameData.Items;
using NorthIsland.Protocol.Messages.Items;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.Features.Crafting;

/// <summary>Handles the compound requests: alchemy with 2 to 5 bag stacks, and mounting a stone in a piece of equipment.</summary>
/// <param name="items">The item data.</param>
/// <param name="lifeSkills">The life skills, trained by a compound that makes an item.</param>
/// <param name="logger">The log.</param>
public sealed class CompoundHandlers(
    ItemData items,
    LifeSkills lifeSkills,
    ILogger<CompoundHandlers> logger)
{
    /// <summary>Random source for compounding: returns a number from 0 up to but not including its argument. Replaceable by tests.</summary>
    public Func<int, int> Roll { get; set; } = GameRandom.Shared.Next;

    /// <summary>Skill ids of Primary Alchemy, Junior Alchemy and High Alchemy.</summary>
    public const ushort PrimaryAlchemy = 15997, JuniorAlchemy = 15998, HighAlchemy = 15999; // 15997 Primary Alchemy, 15998 Junior Alchemy, 15999 Superior Alchemy

    /// <summary>Finds the alchemy skill a compound request trains: the skill of the form used, or the other of the two when only that one is known.</summary>
    /// <param name="sub">The action 23 sub code the request came with.</param>
    /// <param name="character">The character who compounds.</param>
    /// <returns>The skill id; 0 for the bless form, which trains nothing.</returns>
    public static ushort SkillTrained(byte sub, Character character) => sub switch
    {
        Action23.Compound => PrimaryAlchemy,
        Action23.CompoundJunior => character.Skill(JuniorAlchemy) is not null ? JuniorAlchemy : HighAlchemy,
        Action23.CompoundHigh => character.Skill(HighAlchemy) is not null ? HighAlchemy : JuniorAlchemy,
        _ => 0,
    };

    /// <summary>Works out the alchemy skill level a compound request gets from the skills the character has.</summary>
    /// <param name="sub">The action 23 sub code the request came with.</param>
    /// <param name="character">The character who compounds.</param>
    /// <returns>2 for the high form with High Alchemy, 1 for the junior or high form with either skill or the plain form with Primary Alchemy, otherwise 0.</returns>
    public static int SkillLevel(byte sub, Character character) => sub switch
    {
        Action23.CompoundHigh when character.Skill(HighAlchemy) is not null => 2, // high form with High Alchemy: level 2
        Action23.CompoundJunior or Action23.CompoundHigh
            when character.Skill(HighAlchemy) is not null || character.Skill(JuniorAlchemy) is not null => 1, // either form with either skill: level 1
        Action23.Compound when character.Skill(PrimaryAlchemy) is not null => 1, // plain form with Primary Alchemy: level 1
        _ => 0, // any other case: level 0
    };

    /// <summary>Works out which rules a compound request follows from the skills the character has.</summary>
    /// <param name="sub">The action 23 sub code the request came with.</param>
    /// <param name="character">The character who compounds.</param>
    /// <returns>The rules of the compound.</returns>
    public static AlchemyForm FormOf(byte sub, Character character) => (sub, SkillLevel(sub, character)) switch
    {
        (Action23.CompoundBless, _) => AlchemyForm.Bless,
        (Action23.CompoundHigh, 2) => AlchemyForm.Superior,
        (Action23.CompoundJunior or Action23.CompoundHigh, 1) => AlchemyForm.Junior,
        _ => AlchemyForm.Primary,
    };

    /// <summary>
    /// Tells whether a compound request is a stone mount rather than alchemy: any stone among the chosen items makes it one, even though equipment
    /// now has material classes of its own.
    /// </summary>
    /// <param name="records">The item records of the chosen stacks.</param>
    /// <returns>True when a stone is among them.</returns>
    public static bool IsMount(IReadOnlyList<ItemRecord> records) => records.Any(Gems.IsStone);

    /// <summary>
    /// Handles a compound request: mounts a stone when one is among the stacks, otherwise compounds the materials and replaces them with the product.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="sub">The action 23 sub code, which tells the junior, high, bless or plain form.</param>
    /// <param name="body">The packet body: the stack count, then that many bag slots.</param>
    internal ValueTask OnCompound(GameSession session, byte sub, ReadOnlyMemory<byte> body)
    {
        var character = session.ActiveCharacter;
        if (session.State != SessionState.InWorld || character is null || session.Battle is not null || body.Length < 1)
            return ValueTask.CompletedTask;
        var n = body.Span[0];
        if (n is < 2 or > 5 || body.Length < 1 + n)
            return ValueTask.CompletedTask;
        var slots = body.Span.Slice(1, n).ToArray();
        var stacks = slots.Distinct().Select(s => character.Bag.Find(b => b.Slot == s)).ToList();
        if (stacks.Count != n || stacks.Exists(s => s is null || items[s.ItemId] is null))
        {
            logger.LogWarning("[{Session}] {Name}: compound with empty, unknown or repeated bag slots {Slots}", session.Id, character.Name,
                string.Join(",", slots));
            return ValueTask.CompletedTask;
        }
        var records = stacks.Select(s => items[s!.ItemId]!).ToList();
        var books = records.Where(Alchemy.IsBook).ToList();
        var materials = records.Where(r => !Alchemy.IsBook(r)).ToList();
        var names = string.Join(" + ", records.Select(r => $"{r.Name.Trim()} (r{r.ShopColumn})"));
        if (IsMount(records))
            return Mount(session, character, stacks!, records, names);
        var bless = materials.Count(Alchemy.IsBlessItem);
        if (books.Count > 1 || materials.Count < 2 || Alchemy.IsBook(records[0])
            || (sub == Action23.CompoundBless) != (bless > 0) || bless > 0 && !Alchemy.SameBlessKind(records))
        {
            logger.LogInformation("[{Session}] {Name} compounds {Materials}: not allowed", session.Id, character.Name, names);
            return ValueTask.CompletedTask;
        }
        if (bless == 0 && materials.Exists(m => Alchemy.Class(m, 0) == 0))
        {
            logger.LogInformation(
                "[{Session}] {Name} compounds {Materials}: a material has no class (equipment), nothing happens",
                session.Id,
                character.Name,
                names);
            return ValueTask.CompletedTask;
        }

        var level = SkillLevel(sub, character) + books.Sum(b => b.ShopColumn);
        var counts = stacks.Where((_, i) => !Alchemy.IsBook(records[i])).Select(s => (int)s!.Count).ToList();
        var result = Alchemy.Compound(materials, level, items.All, Roll, character.Skill(SkillTrained(sub, character))?.Level ?? 1, FormOf(sub, character), counts,
            books.Count > 0);
        var main = stacks[0]!;
        switch (result.Outcome)
        {
            case AlchemyOutcome.Made: // made: the materials are replaced by the product
                if (!Replace(session, character, stacks!, result.Product!.Id))
                    return NoRoom(session, character, names);
                if (result.Product.ShopColumn > materials.Min(m => m.ShopColumn)) // only a result above the lowest material counts as a success
                    lifeSkills.Train(session, SkillTrained(sub, character));
                break;
            case AlchemyOutcome.Degraded when Alchemy.Degraded(records[0], items.All, Roll) is { } lower: // degraded with a random lower item: it replaces the main material
                if (!Replace(session, character, stacks!, lower.Id))
                    return NoRoom(session, character, names);
                result = result with { Product = lower };
                break;
            case AlchemyOutcome.Degraded: // degraded with no lower item: handled as a byproduct
            case AlchemyOutcome.Stone: // byproduct: the materials are replaced by the byproduct of their class
                var byproduct = Alchemy.Byproduct(result.MainClass);
                if (!Replace(session, character, stacks!, byproduct))
                    return NoRoom(session, character, names);
                result = result with { Outcome = AlchemyOutcome.Stone, Product = items[byproduct] };
                break;
        }
        logger.LogInformation("[{Session}] {Name} compounds {Materials} at level {Level} (rank {Target}): {Outcome}{Product}", session.Id,
            character.Name, names, level, result.TargetRank, result.Outcome,
            result.Product is { } p ? $" {p.Name.Trim()} (r{p.ShopColumn})" : "");
        return ValueTask.CompletedTask;
    }

    /// <summary>Mounts a stone in a piece of equipment when the pair is allowed, and tells the client.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The character who mounts.</param>
    /// <param name="stacks">The chosen bag stacks.</param>
    /// <param name="records">The item records of the stacks, in the same order.</param>
    /// <param name="names">The names of the chosen items, for the log.</param>
    private ValueTask Mount(
        GameSession session,
        Character character,
        List<BagItem> stacks,
        List<ItemRecord> records,
        string names)
    {
        var equipment = records.FindIndex(r => r.IsEquipment);
        var stone = records.FindIndex(Gems.IsStone);
        if (stacks.Count != 2 || equipment < 0 || stone < 0)
        {
            logger.LogInformation("[{Session}] {Name} mounts {Materials}: needs one piece of equipment and one stone", session.Id, character.Name, names);
            return ValueTask.CompletedTask;
        }
        var target = stacks[equipment];
        if (Gems.CanMount(records[equipment], target.Insert, records[stone]) is var refusal and not GemRefusal.None)
        {
            logger.LogInformation("[{Session}] {Name} mounts {Materials}: refused ({Refusal})", session.Id, character.Name, names, refusal);
            return ValueTask.CompletedTask;
        }
        var gem = stacks[stone];
        gem.Count--;
        if (gem.Count == 0)
            character.Bag.Remove(gem);
        target.Insert = records[stone].Id;
        session.Send(ItemMessages.BagSlotDecrease(gem.Slot, 1));
        session.Send(ItemMessages.CrystalMounted(target.Slot, records[stone].Id));
        logger.LogInformation("[{Session}] {Name} mounts {Stone} in {Equipment} (bag slot {Slot})", session.Id, character.Name,
            records[stone].Name.Trim(), records[equipment].Name.Trim(), target.Slot);
        return ValueTask.CompletedTask;
    }

    /// <summary>Logs that a compound was dropped because the product would not fit in the bag.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The character who compounds.</param>
    /// <param name="names">The names of the chosen items, for the log.</param>
    private ValueTask NoRoom(GameSession session, Character character, string names)
    {
        logger.LogInformation("[{Session}] {Name} compounds {Materials}: no room for the product", session.Id, character.Name, names);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Uses up every stack and puts as many products in the bag as the smallest stack held, or one for equipment, but only when they all fit once the
    /// materials are gone.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The character who compounds.</param>
    /// <param name="used">The bag stacks that are used up; the smallest sets how many products are made.</param>
    /// <param name="product">The item id of the product.</param>
    /// <returns>True when the exchange was done; false, with nothing changed, when the product has no room.</returns>
    private bool Replace(GameSession session, Character character, List<BagItem> used, ushort product)
    {
        var bag = character.Bag.Select(
            b => new BagItem { Slot = b.Slot, ItemId = b.ItemId, Count = b.Count, Wear = b.Wear, Fusion = b.Fusion, Pose = b.Pose, BoxItem = b.BoxItem })
            .ToList();
        foreach (var stack in used)
            bag.Remove(bag.Find(b => b.Slot == stack.Slot)!);
        var count = items[product]?.IsEquipment == true ? 1 : used.Min(s => s.Count);
        if (Bag.Add(bag, items, product, count).Sum(c => c.Added) != count)
            return false;

        foreach (var stack in used)
        {
            character.Bag.Remove(stack);
            session.Send(ItemMessages.BagSlotDecrease(stack.Slot, stack.Count));
        }
        var added = Bag.Add(character, items, product, count);
        foreach (var (stack, gained) in added)
            session.Send(ItemMessages.BagAdd(new BagEntry { Slot = stack.Slot, ItemId = product, Count = gained, Wear = stack.Wear }));
        if (added.Count > 0)
            session.Send(ItemMessages.CompoundResult(product, added[0].Item.Slot));
        return true;
    }
}
