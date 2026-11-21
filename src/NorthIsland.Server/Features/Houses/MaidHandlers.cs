using System.Collections.Frozen;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using NorthIsland.Protocol.IO;
using NorthIsland.Protocol.Messages.Items;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.Features.Houses;

/// <summary>The Wit Maid pieces in a house: the bank, the pet storage and a change of clothes, all from the maid's talk menu.</summary>
/// <param name="tents">The house and furniture handlers, for house owners.</param>
/// <param name="storage">The pet storage handlers, for the pet storage form.</param>
/// <param name="logger">The logger.</param>
public sealed class MaidHandlers(
    TentHandlers tents,
    PetStorageHandlers storage,
    ILogger<MaidHandlers> logger)
{
    /// <summary>The item ids of the maid piece and of its costumes.</summary>
    public static readonly ImmutableArray<ushort> MaidFurniture = [39096, // Wit Maid
        39117, // Wit Maid-Summer
        39118, // Wit Maid-Rabbit
        39119, // Wit Maid-Cat
        39223, // Wit Maid-Pumpkin
        39224, // Wit Maid-Succubus
        39243]; // Wit Maid-Qi Pao

    /// <summary>The costumes by choice number: the suit item the player needs and the maid piece that results.</summary>
    public static readonly FrozenDictionary<int, (ushort Suit, ushort Maid)> Costumes = new Dictionary<int, (ushort Suit, ushort Maid)>
    {
        [
            0] = (34175, 39096), [ // 34175 Seasonal Suit, 39096 Wit Maid
                1] = (34175, 39117), // 34175 Seasonal Suit, 39117 Wit Maid-Summer
        [2] = (34176, 39118), // 34176 Rabbit Suit, 39118 Wit Maid-Rabbit
        [3] = (34177, 39119), // 34177 Cat Suit, 39119 Wit Maid-Cat
        [5] = (34411, 39223), // 34411 Pumpkin Suit, 39223 Wit Maid-Pumpkin
        [6] = (34412, 39224), // 34412 Succubus Suit, 39224 Wit Maid-Succubus
        [7] = (34417, 39243), // 34417 Maid Qi Pao, 39243 Wit Maid-Qi Pao
    }.ToFrozenDictionary();

    /// <summary>Handles <c>76/250</c>: answers a menu close with the packet that closes the maid's window.</summary>
    /// <param name="session">The session.</param>
    /// <param name="body">The packet body, not used.</param>
    internal static ValueTask OnClosed(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State == SessionState.InWorld)
            session.Send(new PacketWriter(Action76.Code, Action76.Hide).ToArray());
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Handles the maid's menu orders: the bank (<c>76/1</c>), the pet storage (<c>76/2</c>) and the change of clothes (<c>76/3</c>). The bank
    /// and the pet storage are recorded as open on the house map (<see cref="OpenServices"/>), which serves their requests.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="body">The maid's furniture index, and for the change of clothes the choice and the bag slot of the suit.</param>
    /// <param name="kind">The order, one of the <see cref="NorthIsland.Protocol.Opcodes.Action76"/> sub codes.</param>
    internal ValueTask OnOrder(GameSession session, ReadOnlyMemory<byte> body, byte kind)
    {
        if (session.State != SessionState.InWorld || session.ActiveCharacter is not { } c || session.Account is null || body.Length < 1
            || !TentHandlers.IsHouseMap(c.MapId) || session.Battle is not null || tents.OwnerOfHouse(c.MapId) is not { } owner)
            return ValueTask.CompletedTask;
        var furn = body.Span[0];
        var piece = owner.Character.Furniture.Find(f => f.Index == furn && !f.Retained);
        if (piece is null || MaidFurniture.IndexOf(piece.ItemId) < 0)
            return ValueTask.CompletedTask;
        switch (kind)
        {
            case Action76.Bank: // 76/1: open the bank
                OpenServices.Open(session, c, ServiceWindow.BankItems);
                BankHandlers.SendItems(session, c);
                session.Send(new PacketWriter(Action76.Code, Action76.Bank).ToArray());
                break;
            case Action76.Pets: // 76/2: open the pet storage
                OpenServices.Open(session, c, ServiceWindow.PetStorage);
                storage.SendState(session, c);
                session.Send(new PacketWriter(Action76.Code, Action76.Pets).ToArray());
                break;
            default: // 76/3: change the maid's clothes
                ChangeClothes(session, c, owner, piece, body.Span);
                break;
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Changes the maid into a costume if the owner has the suit in the bag, using one suit up; otherwise closes the maid's window. The piece
    /// becomes the costume's maid item, as the client shows it, so taking it back gives that item.
    /// </summary>
    /// <param name="session">The owner's session.</param>
    /// <param name="c">The player's character.</param>
    /// <param name="owner">The owner of the house.</param>
    /// <param name="piece">The maid piece.</param>
    /// <param name="body">The packet body: furniture index, choice and bag slot.</param>
    private void ChangeClothes(
        GameSession session,
        Character c,
        TentOwner owner,
        Furniture piece,
        ReadOnlySpan<byte> body)
    {
        var (choice, bagSlot) = body.Length >= 3 ? (body[1], body[2]) : ((byte)255, (byte)0);
        if (!ReferenceEquals(owner.Character, c) || !Costumes.TryGetValue(choice, out var costume)
            || c.Bag.Find(b => b.Slot == bagSlot) is not { } suit || suit.ItemId != costume.Suit || piece.ItemId == costume.Maid
            || BagLocks.IsLocked(session, bagSlot))
        {
            logger.LogWarning("[{Session}] {Name}: maid {Furn} costume {Choice} from bag slot {Slot}: refused ({Why})", session.Id, c.Name,
                piece.Index, choice, bagSlot, BagLocks.LockedBy(session, bagSlot) ?? "not the owner, no such costume or suit, or already worn");
            session.Send(new PacketWriter(Action76.Code, Action76.Hide).ToArray());
            return;
        }
        suit.Count--;
        if (suit.Count == 0)
            c.Bag.Remove(suit);
        session.Send(ItemMessages.BagSlotDecrease(suit.Slot, 1));
        piece.ItemId = costume.Maid;
        session.Send(new PacketWriter(Action76.Code, Action76.Costume).U8((byte)piece.Index).U16(costume.Maid).ToArray());
        logger.LogInformation("[{Session}] {Name}: the maid {Furn} changes into {Item}", session.Id, c.Name, piece.Index, costume.Maid);
    }
}
