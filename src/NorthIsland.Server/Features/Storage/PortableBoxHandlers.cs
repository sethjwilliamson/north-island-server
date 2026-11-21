using Microsoft.Extensions.Logging;
using NorthIsland.GameData.Items;
using NorthIsland.Protocol.Messages.Items;
using NorthIsland.Protocol.Messages.Player;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.Features.Storage;

/// <summary>Serves the Picnic Box and the EXP Bag, two bag items that hold a few stacks of food or experience drinks and apply them on use.</summary>
/// <param name="items">The item records.</param>
/// <param name="effects">The item effect handlers, used to drink an experience item.</param>
/// <param name="logger">The logger.</param>
public sealed class PortableBoxHandlers(
    ItemData items,
    ItemEffectHandlers effects,
    ILogger<PortableBoxHandlers> logger)
{
    /// <summary>The item ids of the Picnic Box and of the EXP Bag.</summary>
    public const ushort PicnicBox = 34376, ExpBag = 34377; // 34376 Picnic Box, 34377 EXP Bag

    /// <summary>The number of cells of the Picnic Box (15) and of the EXP Bag (10), and the most items one cell holds (50).</summary>
    public const int PicnicCells = 15, ExpCells = 10, MaxStack = 50;

    /// <summary>Sends the stored cells of both boxes to a player who enters the world.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="c">The character that entered.</param>
    public static void SendOnLogin(GameSession session, Character c)
    {
        foreach (var s in c.PicnicSlots)
            session.Send(PortableBoxMessages.Added(true, s.Cell, s.ItemId, s.Count));
        foreach (var s in c.ExpBagSlots)
            session.Send(PortableBoxMessages.Added(false, s.Cell, s.ItemId, s.Count));
    }

    /// <summary>
    /// Tells whether an item may be put in a box: food that restores HP or SP for the Picnic Box, an item of equip slot 8 and sub type 127 for the
    /// EXP Bag.
    /// </summary>
    /// <param name="picnic">True for the Picnic Box, false for the EXP Bag.</param>
    /// <param name="r">The item record.</param>
    /// <returns>True when the box takes the item.</returns>
    private static bool Accepts(bool picnic, ItemRecord r) =>
        picnic ? r.Type is 23 or 32 && r.Restores.Any(x => x.Attr is Action8.AttrHp or Action8.AttrSp) : r.EquipSlot == 8 && r.SubType == 127;

    /// <summary>
    /// Handles an order for one of the boxes: put a bag stack in, take a cell out, or use a cell. Needs the box item in the bag slot the packet
    /// names, and no battle.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: u8 operation (1 put, 2 take, 3 use), its arguments, and the bag slot of the box.</param>
    /// <param name="picnic">True for the Picnic Box, false for the EXP Bag.</param>
    internal ValueTask Handle(GameSession session, ReadOnlyMemory<byte> body, bool picnic)
    {
        if (session.State != SessionState.InWorld || session.ActiveCharacter is not { } c || body.Length < 4 || session.Battle is not null)
            return ValueTask.CompletedTask;
        var cells = picnic ? c.PicnicSlots : c.ExpBagSlots;
        var (op, rest) = (body.Span[0], body.Span[1..].ToArray());
        var boxSlot = op == 1 ? (rest.Length >= 4 ? rest[3] : (byte)0) : rest.Length >= 3 ? rest[2] : (byte)0;
        if (c.Bag.Find(b => b.Slot == boxSlot)?.ItemId != (picnic ? PicnicBox : ExpBag))
        {
            logger.LogInformation("[{Session}] {Name}: box operation {Op} without the box in slot {Slot}", session.Id, c.Name, op, boxSlot);
            return ValueTask.CompletedTask;
        }
        switch (op)
        {
            case 1 when rest.Length >= 4: // put
                Put(session, c, cells, picnic, rest[0], rest[1]);
                break;
            case 2: // take
                Take(session, c, cells, picnic, rest[0]);
                break;
            case 3: // use
                Use(session, c, cells, picnic, rest[0], rest[1]);
                break;
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Moves part or all of a bag stack into a box cell, up to 50 in the cell; refused for an item the box does not take, a cell holding another
    /// item, a partly used stack, since a cell keeps only the item and its count, or a stack committed elsewhere (<see cref="BagLocks"/>).
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="c">The player's character.</param>
    /// <param name="cells">The cells of the box.</param>
    /// <param name="picnic">True for the Picnic Box, false for the EXP Bag.</param>
    /// <param name="bagSlot">The bag slot to take from.</param>
    /// <param name="cell">The box cell to put into, 1-based.</param>
    private void Put(
        GameSession session,
        Character c,
        List<BoxSlot> cells,
        bool picnic,
        byte bagSlot,
        byte cell)
    {
        var stack = c.Bag.Find(b => b.Slot == bagSlot);
        var record = stack is null ? null : items[stack.ItemId];
        var current = cells.Find(x => x.Cell == cell);
        var locked = BagLocks.LockedBy(session, bagSlot);
        if (stack is null || record is null || cell < 1 || cell > (picnic ? PicnicCells : ExpCells) || !Accepts(picnic, record)
            || stack.Used != 0 || stack.Extra != 0 || locked is not null
            || (current is not null && (current.ItemId != stack.ItemId || current.Count >= MaxStack)))
        {
            logger.LogInformation(
                "[{Session}] {Name}: box put refused (slot {Slot}, cell {Cell}{Locked})",
                session.Id,
                c.Name,
                bagSlot,
                cell,
                locked is null ? "" : $", {locked}");
            return;
        }
        if (current is null)
            cells.Add(current = new BoxSlot { Cell = cell, ItemId = stack.ItemId });
        var moved = (byte)Math.Min(stack.Count, MaxStack - current.Count);
        current.Count += moved;
        stack.Count -= moved;
        session.Send(ItemMessages.BagSlotDecrease(stack.Slot, moved));
        if (stack.Count == 0)
            c.Bag.Remove(stack);
        session.Send(PortableBoxMessages.Added(picnic, cell, current.ItemId, moved));
    }

    /// <summary>Moves the whole content of a box cell back to the bag, when it all fits.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="c">The player's character.</param>
    /// <param name="cells">The cells of the box.</param>
    /// <param name="picnic">True for the Picnic Box, false for the EXP Bag.</param>
    /// <param name="cell">The box cell to empty.</param>
    private void Take(
        GameSession session,
        Character c,
        List<BoxSlot> cells,
        bool picnic,
        byte cell)
    {
        var current = cells.Find(x => x.Cell == cell);
        if (current is null)
            return;
        var trial = c.Bag.Select(b => new BagItem { Slot = b.Slot, ItemId = b.ItemId, Count = b.Count, Wear = b.Wear }).ToList();
        if (Bag.Add(trial, items, current.ItemId, current.Count).Sum(x => x.Added) != current.Count)
        {
            logger.LogInformation("[{Session}] {Name}: no room for the {Count} items of cell {Cell}", session.Id, c.Name, current.Count, cell);
            return;
        }
        foreach (var (stack, added) in Bag.Add(c, items, current.ItemId, current.Count))
            session.Send(ItemMessages.BagAdd(new BagEntry { Slot = stack.Slot, ItemId = stack.ItemId, Count = added, Wear = stack.Wear }));
        cells.Remove(current);
        session.Send(PortableBoxMessages.Removed(picnic, cell));
    }

    /// <summary>
    /// Uses one item of a box cell. The Picnic Box restores HP and SP of the player or of a pet; the EXP Bag drinks an experience item.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="c">The player's character.</param>
    /// <param name="cells">The cells of the box.</param>
    /// <param name="picnic">True for the Picnic Box, false for the EXP Bag.</param>
    /// <param name="cell">The box cell to use from.</param>
    /// <param name="arg">The slot of the pet to restore in the Picnic Box, 0 for the player.</param>
    private void Use(
        GameSession session,
        Character c,
        List<BoxSlot> cells,
        bool picnic,
        byte cell,
        byte arg)
    {
        var current = cells.Find(x => x.Cell == cell);
        var record = current is null ? null : items[current.ItemId];
        if (current is null || record is null || !Accepts(picnic, record))
            return;
        if (picnic)
        {
            var pet = arg == 0 ? null : c.Pets.Find(p => p.Slot == arg);
            if (arg != 0 && pet is null)
                return;
            var (hp, maxHp, sp, maxSp) = pet is null ? (c.Hp, c.MaxHp, c.Sp, c.MaxSp) : (pet.Hp, pet.MaxHp, pet.Sp, pet.MaxSp);
            var gainHp = Math.Min(maxHp - hp, record.Restores.Where(x => x.Attr == Action8.AttrHp).Sum(x => x.Amount));
            var gainSp = Math.Min(maxSp - sp, record.Restores.Where(x => x.Attr == Action8.AttrSp).Sum(x => x.Amount));
            if (gainHp <= 0 && gainSp <= 0)
                return;
            if (pet is null)
            {
                c.Hp += gainHp;
                c.Sp += gainSp;
                session.Send(InnMessages.SetAttribute(Action8.AttrHp, c.Hp));
                session.Send(InnMessages.SetAttribute(Action8.AttrSp, c.Sp));
            }
            else
            {
                pet.Hp += gainHp;
                pet.Sp += gainSp;
                session.Send(PetMessages.SetAttribute(pet.Slot, Action8.AttrHp, pet.Hp));
                session.Send(PetMessages.SetAttribute(pet.Slot, Action8.AttrSp, pet.Sp));
            }
            session.Send(PortableBoxMessages.PicnicUsed(cell, arg, (ushort)Math.Max(0, gainHp), (ushort)Math.Max(0, gainSp)));
        }
        else
        {
            if (!effects.DrinkExp(session, c, record))
                return;
            session.Send(PortableBoxMessages.ExpUsed(cell));
        }
        current.Count--;
        if (current.Count == 0)
            cells.Remove(current);
        logger.LogInformation("[{Session}] {Name} uses {Item} from the {Box}", session.Id, c.Name, record.Name.Trim(), picnic ? "Picnic Box" : "EXP Bag");
    }
}
