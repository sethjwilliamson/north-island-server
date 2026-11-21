using Microsoft.Extensions.Logging;
using NorthIsland.Protocol.IO;
using NorthIsland.Protocol.Messages.Items;
using NorthIsland.Protocol.Messages.Player;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.Features.PetCare;

/// <summary>Turns a companion voucher into a pet in the player's team.</summary>
/// <param name="items">The item data, to look up the voucher.</param>
/// <param name="npcs">The NPC data, to look up the pet.</param>
/// <param name="formulas">The formula data, for the new pet's HP and SP.</param>
/// <param name="logger">The logger.</param>
public sealed class PetVoucherHandlers(
    ItemData items,
    NpcData npcs,
    FormulaData formulas,
    ILogger<PetVoucherHandlers> logger)
{
    /// <summary>The item sub type of companion vouchers.</summary>
    public const byte VoucherSubType = 146;

    /// <summary>Gets the pet a voucher makes.</summary>
    /// <param name="item">The item record.</param>
    /// <returns>The pet's NPC id, or 0 when the item is not a companion voucher.</returns>
    public static ushort PetOf(NorthIsland.GameData.Items.ItemRecord item) =>
        item.SubType == VoucherSubType && item.Words415.Length > 2 ? item.Words415[2] : (ushort)0;

    /// <summary>
    /// Uses a voucher: the pet joins the team in the first free slot and the voucher is used up; a full team or a pet of the same kind
    /// (on the team, in the storage or asleep in a bed) refuses it.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the bag slot of the voucher.</param>
    internal ValueTask OnUse(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || session.ActiveCharacter is not { } c || body.Length < 1 || session.Battle is not null)
            return ValueTask.CompletedTask;
        var stack = c.Bag.Find(b => b.Slot == body.Span[0]);
        var item = stack is null ? null : items[stack.ItemId];
        var npcId = item is null ? (ushort)0 : PetOf(item);
        if (stack is null || item is null || npcId == 0 || npcs[npcId] is not { } npc)
        {
            logger.LogInformation("[{Session}] {Name}: bag slot {Slot} is not a companion voucher", session.Id, c.Name, body.Span[0]); // Slot
            return ValueTask.CompletedTask;
        }
        var why = c.Pets.Count >= Pet.MaxPets ? "the team is full" : c.Pets.Exists(p => p.NpcId == npc.Id) ? "already has one"
            : c.StoredPets.Exists(p => p.NpcId == npc.Id) ? "the pet keeper has one"
                : Pets.SleepsInBed(c, npc.Id) ? "one sleeps in a bed" : null;
        if (why is not null)
        {
            logger.LogInformation("[{Session}] {Name}: {Item} refused ({Why})", session.Id, c.Name, item.Name.Trim(), why);
            session.Send(
                new PacketWriter(
                    Action83.Code,
                    c.Pets.Count >= Pet.MaxPets ? Action83.PetSlotsFull : why == "already has one" ? Action83.SamePet : Action83.KeeperHasPet)
                    .ToArray());
            return ValueTask.CompletedTask;
        }
        stack.Count--;
        if (stack.Count == 0)
            c.Bag.Remove(stack);
        session.Send(ItemMessages.BagSlotDecrease(stack.Slot, 1));
        var pet = Pets.Create(npc, Pets.FreeSlot(c), formulas.Table);
        c.Pets.Add(pet);
        session.Send(PetMessages.Joined(session.Account!.LoginId, Pets.Info(pet)));
        logger.LogInformation(
            "[{Session}] {Name} uses {Item}: {Pet} ({Npc}) joins the team in slot {Slot}",
            session.Id,
            c.Name,
            item.Name.Trim(),
            pet.Name,
            pet.NpcId,
            pet.Slot);
        return ValueTask.CompletedTask;
    }
}
