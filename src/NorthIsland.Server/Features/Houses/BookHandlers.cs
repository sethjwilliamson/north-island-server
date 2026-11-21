using Microsoft.Extensions.Logging;
using NorthIsland.Protocol.IO;
using NorthIsland.Protocol.Messages.Items;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.Features.Houses;

/// <summary>The Bookcase and the Desk in a house, which hold up to ten blueprints each for the manufacture orders.</summary>
/// <param name="items">The item data, to check that an item is a blueprint.</param>
/// <param name="tents">The house and furniture handlers, for the house entry event.</param>
/// <param name="logger">The logger.</param>
public sealed class BookHandlers(
    ItemData items,
    TentHandlers tents,
    ILogger<BookHandlers> logger)
{
    /// <summary>The item ids of the Bookcase and the Desk.</summary>
    public const ushort Bookcase = 39054, Desk = 39020; // 39054 Bookcase, 39020 Desk

    /// <summary>The number of blueprint slots in the Bookcase and in the Desk.</summary>
    public const int Slots = 10;

    /// <summary>Hooks the house entry event so the owner is shown the stored blueprints.</summary>
    internal void Start()
    {
        tents.HouseEntered += OnHouseEntered;
    }

    /// <summary>The ten blueprint slots of a character's Bookcase or Desk.</summary>
    /// <param name="c">The character.</param>
    /// <param name="desk">True for the Desk, false for the Bookcase.</param>
    /// <returns>The item ids by slot, 0 for an empty slot.</returns>
    private static ushort[] Slot(Character c, bool desk) => desk ? c.BookDesk : c.BookCase;

    /// <summary>Builds the packet that tells the client what a Bookcase or Desk slot holds.</summary>
    /// <param name="slot">The slot number, from 1.</param>
    /// <param name="item">The item id, 0 when the slot is empty.</param>
    /// <param name="desk">True for the Desk (<c>5/24</c>), false for the Bookcase (<c>5/13</c>).</param>
    /// <returns>The packet.</returns>
    private static byte[] Slot(byte slot, ushort item, bool desk) =>
        new PacketWriter(Action5.Code, desk ? Action5.DeskSlot : Action5.BookcaseSlot).U8(slot).U16(item).ToArray();

    /// <summary>The active character of a session that is in the world and stands in their own house with the Bookcase or Desk placed.</summary>
    /// <param name="session">The session.</param>
    /// <param name="desk">True to ask for the Desk, false for the Bookcase.</param>
    /// <returns>The character, or null when the conditions do not hold.</returns>
    private static Character? InOwnHouse(GameSession session, bool desk)
    {
        var c = session.ActiveCharacter;
        return session.State == SessionState.InWorld
            && c is not null
            && session.Account is not null
            && TentHandlers.HouseMapOf(session.Account.LoginId) == c.MapId
            && c.Furniture.Exists(f => f.ItemId == (desk ? Desk : Bookcase) && !f.Retained) ? c : null;
    }

    /// <summary>Handles the order that moves a blueprint from the bag into the Bookcase (<c>5/9</c>) or the Desk (<c>5/11</c>).</summary>
    /// <param name="session">The owner's session.</param>
    /// <param name="body">The bag slot and the target slot.</param>
    /// <param name="desk">True for the Desk, false for the Bookcase.</param>
    internal ValueTask Put(GameSession session, ReadOnlyMemory<byte> body, bool desk)
    {
        if (InOwnHouse(session, desk) is not { } c || body.Length < 2)
            return ValueTask.CompletedTask;
        var (bagSlot, slot) = (body.Span[0], body.Span[1]);
        var stack = c.Bag.Find(b => b.Slot == bagSlot);
        var held = Slot(c, desk);
        if (stack is null || slot is < 1 or > Slots || held[slot - 1] != 0 || items[stack.ItemId]?.SubType != 103) // 103: the blueprint sub type
            return ValueTask.CompletedTask;
        if (BagLocks.LockedBy(session, bagSlot) is { } locked)
        {
            logger.LogWarning("[{Session}] {Name}: blueprint from bag slot {Slot}: it is {Why}", session.Id, c.Name, bagSlot, locked);
            return ValueTask.CompletedTask;
        }
        held[slot - 1] = stack.ItemId;
        stack.Count--;
        if (stack.Count == 0)
            c.Bag.Remove(stack);
        session.Send(ItemMessages.BagSlotDecrease(bagSlot, 1));
        session.Send(Slot(slot, held[slot - 1], desk));
        return ValueTask.CompletedTask;
    }

    /// <summary>Handles the order that moves a blueprint from the Bookcase (<c>5/10</c>) or the Desk (<c>5/12</c>) back into the bag.</summary>
    /// <param name="session">The owner's session.</param>
    /// <param name="body">The slot to empty.</param>
    /// <param name="desk">True for the Desk, false for the Bookcase.</param>
    internal ValueTask Take(GameSession session, ReadOnlyMemory<byte> body, bool desk)
    {
        if (InOwnHouse(session, desk) is not { } c || body.Length < 1)
            return ValueTask.CompletedTask;
        var slot = body.Span[0];
        var held = Slot(c, desk);
        if (slot is < 1 or > Slots || held[slot - 1] == 0)
            return ValueTask.CompletedTask;
        var item = held[slot - 1];
        var trial = c.Bag.Select(b => GridStore.Copy(b, b.Slot, b.Count)).ToList();
        if (Bag.Add(trial, items, item, 1).Sum(x => x.Added) != 1)
            return ValueTask.CompletedTask;
        foreach (var (stack, added) in Bag.Add(c.Bag, items, item, 1))
            session.Send(ItemMessages.BagAdd(new BagEntry { Slot = stack.Slot, ItemId = stack.ItemId, Count = added, Wear = stack.Wear }));
        held[slot - 1] = 0;
        session.Send(Slot(slot, 0, desk));
        logger.LogInformation("[{Session}] {Name} takes {Item} from the {Where}", session.Id, c.Name, items[item]?.Name.Trim(), desk ? "desk" : "bookcase");
        return ValueTask.CompletedTask;
    }

    /// <summary>Whether a character has a blueprint in the bag, the bank, the Bookcase or the Desk.</summary>
    /// <param name="c">The character.</param>
    /// <param name="blueprint">The blueprint item id; 0 means the recipe needs none.</param>
    /// <returns>True when the blueprint is needed and found, or none is needed.</returns>
    public static bool HasBlueprint(Character c, ushort blueprint) =>
        blueprint == 0
            || c.Bag.Exists(b => b.ItemId == blueprint)
            || c.Bank.Exists(b => b.ItemId == blueprint)
            || c.BookCase.Contains(blueprint)
            || c.BookDesk.Contains(blueprint);

    /// <summary>Sends the owner the blueprints in their Bookcase and Desk when they enter their house.</summary>
    /// <param name="session">The session that entered the house.</param>
    /// <param name="owner">The house owner's session, or null when the owner is not online.</param>
    private static void OnHouseEntered(GameSession session, GameSession? owner)
    {
        if (owner != session || session.ActiveCharacter is not { } c)
            return;
        for (var i = 0; i < Slots; i++)
        {
            if (c.BookCase[i] != 0)
                session.Send(Slot((byte)(i + 1), c.BookCase[i], desk: false));
            if (c.BookDesk[i] != 0)
                session.Send(Slot((byte)(i + 1), c.BookDesk[i], desk: true));
        }
    }
}
