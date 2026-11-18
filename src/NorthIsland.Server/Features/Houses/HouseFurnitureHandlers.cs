using Microsoft.Extensions.Logging;
using NorthIsland.Protocol.IO;
using NorthIsland.Protocol.Messages.Items;
using NorthIsland.Protocol.Messages.World;
using NorthIsland.Protocol.Text;

namespace NorthIsland.Server.Features.Houses;

/// <summary>Furniture that holds things or makes sound in a house: cupboards, boxes, the Music Center, the piano and the doorplate.</summary>
/// <param name="world">The world state, to find the sessions in a house.</param>
/// <param name="items">The item data, to check what a piece may hold.</param>
/// <param name="tents">The house and furniture handlers, for house owners and house events.</param>
/// <param name="logger">The logger.</param>
public sealed class HouseFurnitureHandlers(
    WorldState world,
    ItemData items,
    TentHandlers tents,
    ILogger<HouseFurnitureHandlers> logger)
{
    /// <summary>The number of slots in the Fridge, the Material Cabinet, the Potion Cabinet and the Medicine Case.</summary>
    public const int BoxSlots = 10;

    /// <summary>The number of slots in the Locker.</summary>
    public const int LockerSlots = 25;

    /// <summary>The number of slots a box of a kind has.</summary>
    /// <param name="kind">The box kind, 1 to 5.</param>
    /// <returns>The slot count.</returns>
    public static int SlotsOf(byte kind) => kind == 5 ? LockerSlots : BoxSlots;

    /// <summary>The box kind a furniture piece opens.</summary>
    /// <param name="furnitureItemId">The item id of the piece.</param>
    /// <returns>
    /// 1 for the Fridge, 2 for the Material Cabinet, 3 for the Potion Cabinet, 4 for the Medicine Case, 5 for the Locker, 0 for other pieces.
    /// </returns>
    public static byte BoxKindOf(ushort furnitureItemId) => furnitureItemId switch
    {
        39023 => 1, // Fridge
        39132 => 2, // Material Cabinet
        39157 => 3, // Potion Cabinet
        39188 => 4, // Medicine Case
        39015 => 5, // Locker
        _ => 0 // any other piece opens no box
    };

    /// <summary>Whether a box of a kind accepts an item.</summary>
    /// <param name="kind">The box kind, 1 to 5.</param>
    /// <param name="item">The item.</param>
    /// <returns>True when the box takes the item.</returns>
    public static bool BoxAccepts(byte kind, NorthIsland.GameData.Items.ItemRecord item) => kind switch
    {
        1 => item.Type is 23 or 32, // Fridge: food, item types 23 and 32
        2 => item.Type is 31 or (>= 33 and <= 38) or 51 || item.Type == 28 && Bag.IsStackable(item), // Material Cabinet: types 31, 33-38, 51 and stackable 28
        3 => item.Id is 32310 // Holy Water
            or 33006 // Poisonous Potion
            or (>= 33017 and <= 33019) // 33017 Strengthening Potion, 33019 Recovery Potion
            or 33022 // Protection Potion
            or 33023 // Cure Potion
            or 33046 // Recovery Medicine
            or 34040 // Strange Eye-drops
            or (>= 34107 and <= 34116) // 34107 1.5X EXP Potion, 34116 6X EXP Potion
            or 34123 // 8X EXP Potion
            or 34131 // 3 Holy EXP Potion
            or 34140 // 1.5 Holy EXP Potion
            or 34143 // 4 Holy EXP Potion
            or 34144 // 5 Holy EXP Potion
            or 34146 // 2 Holy EXP Potion
            or 34147 // 2.5 Holy EXP Potion
            or 34173 // 10X EXP Potion
            or (>= 34185 and <= 34190), // 34185 3.5X Holy EXP Potion, 34190 10X Holy EXP Potion
        4 => item.SubType is 113 or 118 || item.Id is 34026 // Protective EXP Pill
            or 34029 // Protective EXP Bolus
            or 34269 // Potential Pill
            or 34289, // Potential  Pill
        5 => item.Type == 21, // Locker: item type 21
        _ => false, // any other kind takes nothing
    };

    /// <summary>The item ids of the Wardrobe, the Cabinet, the Doorplate, the Music Center and the Piano.</summary>
    public const ushort Wardrobe = 39018, // Wardrobe
        Cabinet = 39019, // Cabinet
        Doorplate = 39056, // Doorplate
        MusicCenter = 39115, // Modular Stereos
        Piano = 39110; // Piano

    /// <summary>The item sub type of cupboard furniture.</summary>
    public const byte CupboardSubType = 109;

    /// <summary>The longest doorplate text, in bytes.</summary>
    public const int MaxDoorplateBytes = 14;

    /// <summary>The number of content ids a character can use over all cupboards.</summary>
    public const int MaxIds = 50;

    /// <summary>Hooks the house events: entry, camps shown and pieces taken back.</summary>
    internal void Start()
    {
        tents.HouseEntered += OnHouseEntered;
        tents.CampsShown += OnCampsShown;
        tents.PiecesTakenBack += OnPiecesTakenBack;
    }

    /// <summary>The number of slots a furniture item has as a cupboard.</summary>
    /// <param name="itemId">The item id.</param>
    /// <returns>5 for the Wardrobe, 10 for the Cabinet, 0 for any other item.</returns>
    public int Capacity(ushort itemId) => items[itemId]?.SubType == CupboardSubType ? itemId switch
    {
        Wardrobe => 5, // Wardrobe holds 5 slots
        Cabinet => 10, // Cabinet holds 10 slots
        _ => 0 // any other item holds none
    } : 0;

    /// <summary>The active character of a session that is in the world and stands in their own house.</summary>
    /// <param name="session">The session.</param>
    /// <returns>The character, or null when the session is not in the world or not in its own house.</returns>
    private static Character? InOwnHouse(GameSession session)
    {
        var character = session.ActiveCharacter;
        return session.State == SessionState.InWorld && character is not null && session.Account is not null
            && TentHandlers.HouseMapOf(session.Account.LoginId) == character.MapId ? character : null;
    }

    /// <summary>Handles <c>62/41</c>: the owner puts a bag stack into a slot of a cupboard.</summary>
    /// <param name="session">The owner's session.</param>
    /// <param name="body">The furniture index (two bytes), the bag slot and the cupboard slot.</param>
    internal ValueTask OnStore(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InOwnHouse(session) is not { } c || body.Length < 4)
            return ValueTask.CompletedTask;
        var r = new PacketReader(body.Span);
        var (furn, bagSlot, slot) = (r.U16(), r.U8(), r.U8());
        var piece = c.Furniture.Find(f => f.Index == furn);
        var stack = c.Bag.Find(b => b.Slot == bagSlot);
        if (piece is null || stack is null || items[stack.ItemId] is not { } item || slot < 1 || slot > Capacity(piece.ItemId)
            || piece.Contents.Exists(x => x.Slot == slot) || (item.Flags & 32) != 0 || item.IsEquipment && stack.Wear >= 200)
        {
            logger.LogInformation(
                "[{Session}] {Name}: cupboard {Furn} store of bag slot {Bag} into slot {Slot} refused",
                session.Id,
                c.Name,
                furn,
                bagSlot,
                slot);
            return ValueTask.CompletedTask;
        }
        var id = FreeId(c);
        if (id == 0)
            return ValueTask.CompletedTask;
        var content = new CupboardItem
        {
            Slot = slot,
            Id = id,
            ItemId = stack.ItemId,
            Count = stack.Count,
            Wear = stack.Wear,
            Insert = stack.Insert,
            Fusion = stack.Fusion
        };
        piece.Contents.Add(content);
        c.Bag.Remove(stack);
        session.Send(ItemMessages.BagSlotDecrease(stack.Slot, stack.Count));
        session.Send(
            HouseLifeMessages.CupboardAdd(
                furn,
                slot,
                id,
                content.ItemId,
                content.Count,
                content.Wear,
                content.Insert,
                content.Fusion,
                content.Pose,
                content.BoxItem));
        logger.LogInformation(
            "[{Session}] {Name} puts {Item} x{Count} into cupboard {Furn} slot {Slot}",
            session.Id,
            c.Name,
            item.Name.Trim(),
            content.Count,
            furn,
            slot);
        return ValueTask.CompletedTask;
    }

    /// <summary>Handles <c>62/42</c>: the owner takes the content of a cupboard slot back into the bag if there is room.</summary>
    /// <param name="session">The owner's session.</param>
    /// <param name="body">The furniture index (two bytes) and the cupboard slot.</param>
    internal ValueTask OnTake(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InOwnHouse(session) is not { } c || body.Length < 3)
            return ValueTask.CompletedTask;
        var r = new PacketReader(body.Span);
        var (furn, slot) = (r.U16(), r.U8());
        var piece = c.Furniture.Find(f => f.Index == furn);
        if (piece?.Contents.Find(x => x.Slot == slot) is not { } content)
            return ValueTask.CompletedTask;
        var trial = c.Bag.Select(
            b =>
                new BagItem
                {
                    Slot = b.Slot,
                    ItemId = b.ItemId,
                    Count = b.Count,
                    Wear = b.Wear,
                    Insert = b.Insert,
                    Fusion = b.Fusion,
                    Pose = b.Pose,
                    BoxItem = b.BoxItem
                })
            .ToList();
        if (Bag.Add(trial, items, content.ItemId, content.Count, content.Wear, content.Insert, content.Fusion, content.Pose, content.BoxItem)
            .Sum(x => x.Added) != content.Count)
        {
            logger.LogInformation("[{Session}] {Name}: no room in the bag for cupboard {Furn} slot {Slot}", session.Id, c.Name, furn, slot);
            return ValueTask.CompletedTask;
        }
        foreach (var (stack, added) in Bag.Add(
            c,
            items,
            content.ItemId,
            content.Count,
            content.Wear,
            content.Insert,
            content.Fusion,
            content.Pose,
            content.BoxItem))
            session.Send(
                ItemMessages.BagAdd(
                    new BagEntry
                    {
                        Slot = stack.Slot,
                        ItemId = stack.ItemId,
                        Count = added,
                        Wear = stack.Wear,
                        Insert = stack.Insert,
                        FusionLevel = stack.Fusion,
                        PoseItemId = stack.Pose,
                        BoxItemId = stack.BoxItem
                    }));
        piece.Contents.Remove(content);
        session.Send(HouseLifeMessages.CupboardRemove(furn, slot, content.Id));
        return ValueTask.CompletedTask;
    }

    /// <summary>Finds an unused cupboard content id.</summary>
    /// <param name="c">The character.</param>
    /// <returns>A free id from 1 to 50, or 0 when all are used.</returns>
    private static byte FreeId(Character c)
    {
        var used = c.Furniture.SelectMany(f => f.Contents).Select(x => x.Id).ToHashSet();
        for (byte id = 1; id <= MaxIds; id++)
            if (!used.Contains(id))
                return id;
        return 0;
    }

    /// <summary>
    /// Handles <c>62/52</c>: the owner puts a stack into, or takes one out of, a box; the first byte gives the box kind and the direction.
    /// </summary>
    /// <param name="session">The owner's session.</param>
    /// <param name="body">The option (1 to 10; odd puts, even takes), the furniture index, the box slot and, for putting, the bag slot.</param>
    internal ValueTask OnBox(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InOwnHouse(session) is not { } c || body.Length < 3 || body.Span[0] is > 10 or 0)
            return ValueTask.CompletedTask;
        var (opt, furn, slot) = (body.Span[0], body.Span[1], body.Span[2]);
        var kind = (byte)(opt is 7 or 8 ? 5 : opt is 9 or 10 ? 4 : (opt + 1) / 2);
        var put = opt is 1 or 3 or 5 or 7 or 9;
        if (BoxKindOf(c.Furniture.Find(f => f.Index == furn)?.ItemId ?? 0) != kind || slot < 1 || slot > SlotsOf(kind))
        {
            logger.LogInformation("[{Session}] {Name}: box order {Opt} on furniture {Furn} slot {Slot} refused", session.Id, c.Name, opt, furn, slot);
            return ValueTask.CompletedTask;
        }
        var held = c.HouseBoxes.Find(b => b.Kind == kind && b.Slot == slot);
        if (put)
        {
            var stack = body.Length >= 4 ? c.Bag.Find(b => b.Slot == body.Span[3]) : null;
            if (held is not null || stack is null || items[stack.ItemId] is not { } item || !BoxAccepts(kind, item))
                return ValueTask.CompletedTask;
            c.HouseBoxes.Add(new BoxItem { Kind = kind, Slot = slot, ItemId = stack.ItemId, Count = stack.Count });
            c.Bag.Remove(stack);
            session.Send(ItemMessages.BagSlotDecrease(stack.Slot, stack.Count));
            session.Send(HouseLifeMessages.BoxSlot(kind, slot, stack.ItemId, stack.Count));
            return ValueTask.CompletedTask;
        }
        if (held is null)
            return ValueTask.CompletedTask;
        var trial = c.Bag.Select(
            b =>
                new BagItem
                {
                    Slot = b.Slot,
                    ItemId = b.ItemId,
                    Count = b.Count,
                    Wear = b.Wear,
                    Insert = b.Insert,
                    Fusion = b.Fusion,
                    Pose = b.Pose,
                    BoxItem = b.BoxItem
                })
            .ToList();
        if (Bag.Add(trial, items, held.ItemId, held.Count).Sum(x => x.Added) != held.Count)
        {
            logger.LogInformation("[{Session}] {Name}: no room in the bag for box {Kind} slot {Slot}", session.Id, c.Name, kind, slot);
            return ValueTask.CompletedTask;
        }
        foreach (var (stack, added) in Bag.Add(c, items, held.ItemId, held.Count))
            session.Send(ItemMessages.BagAdd(new BagEntry { Slot = stack.Slot, ItemId = stack.ItemId, Count = added, Wear = stack.Wear }));
        c.HouseBoxes.Remove(held);
        session.Send(HouseLifeMessages.BoxSlotCleared(kind, slot));
        return ValueTask.CompletedTask;
    }

    /// <summary>Plays the house's song for the player who entered and shows the owner the contents of their boxes and cupboards.</summary>
    /// <param name="session">The session that entered the house.</param>
    /// <param name="owner">The house owner's session, or null when the owner is not online.</param>
    private void OnHouseEntered(GameSession session, GameSession? owner)
    {
        if (session.ActiveCharacter is not { } entrant)
            return;
        if ((owner?.ActiveCharacter ?? tents.OwnerOfHouse(entrant.MapId)?.Character) is { HouseBgmIndex: > 0, HouseBgmFile: { } song } host)
            session.Send(HouseLifeMessages.HouseMusic(host.HouseBgmIndex, song));
        else if (owner == session)
        {
            // no song chosen yet: the owner's client keeps its choice in a file of its own and is asked for it (its first answer is song 1)
            session.HouseSongAsked = true;
            session.Send(HouseLifeMessages.HouseMusicAsk());
        }
        if (owner != session || session.ActiveCharacter is not { } c)
            return;
        foreach (var box in c.HouseBoxes)
            session.Send(HouseLifeMessages.BoxSlot(box.Kind, box.Slot, box.ItemId, box.Count));
        foreach (var piece in c.Furniture)
            foreach (var x in piece.Contents)
                session.Send(HouseLifeMessages.CupboardAdd(piece.Index, x.Slot, x.Id, x.ItemId, x.Count, x.Wear, x.Insert, x.Fusion, x.Pose, x.BoxItem));
    }

    /// <summary>The sessions in the house of a session, including the session itself.</summary>
    /// <param name="session">The session.</param>
    /// <returns>The sessions.</returns>
    private IEnumerable<GameSession> InHouse(GameSession session) =>
        world.OnMap(session.ActiveCharacter!.MapId).Append(session).Distinct();

    /// <summary>Handles <c>62/61</c>: the owner picks a song for the house at the Music Center, or the owner's client answers with its saved song on entering, and the house hears it.</summary>
    /// <param name="session">The owner's session.</param>
    /// <param name="body">The song index (four bytes), then the song file name.</param>
    internal ValueTask OnMusic(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || session.ActiveCharacter is not { } c || session.Account is null || body.Length < 4)
            return ValueTask.CompletedTask;
        var index = new PacketReader(body.Span).I32();
        var file = GameEncoding.GetString(body.Span[4..]).Trim();
        // the answer to the question asked on entering needs no Music Center: a house plays its owner's saved song without one
        var asked = session.HouseSongAsked;
        session.HouseSongAsked = false;
        var allowed = TentHandlers.HouseMapOf(session.Account.LoginId) == c.MapId && (asked || c.Furniture.Exists(f => f.ItemId == MusicCenter))
            && index is >= 0 and <= 1000 && file.Length <= 40 && (index == 0 || file.Length > 0);
        if (!allowed)
        {
            logger.LogInformation("[{Session}] {Name}: music order {Index} refused", session.Id, c.Name, index);
            session.Send(HouseLifeMessages.HouseMusic(c.HouseBgmIndex, c.HouseBgmFile ?? ""));
            return ValueTask.CompletedTask;
        }
        (c.HouseBgmIndex, c.HouseBgmFile) = (index, index == 0 ? null : file);
        var packet = HouseLifeMessages.HouseMusic(index, file);
        foreach (var s in InHouse(session))
            s.Send(packet);
        logger.LogInformation("[{Session}] {Name}'s house plays {Song} ({Index})", session.Id, c.Name, file, index);
        return ValueTask.CompletedTask;
    }

    /// <summary>Handles <c>62/57</c>: sends a piano note to the others in the house.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The note number, 1 to 7.</param>
    internal ValueTask OnPianoKey(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || session.ActiveCharacter is not { } c || body.Length < 1 || body.Span[0] is < 1 or > 7
            || !TentHandlers.IsHouseMap(c.MapId))
            return ValueTask.CompletedTask;
        var packet = HouseLifeMessages.PianoNote(body.Span[0]); // note
        foreach (var s in InHouse(session))
            if (s != session)
                s.Send(packet);
        return ValueTask.CompletedTask;
    }

    /// <summary>Handles <c>23/99</c>: shows the others in the house that a player sat down at the piano.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The furniture index (two bytes).</param>
    internal ValueTask OnPianoSit(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || session.ActiveCharacter is not { } c || session.Account is null || body.Length < 2
            || !TentHandlers.IsHouseMap(c.MapId))
            return ValueTask.CompletedTask;
        var furn = new PacketReader(body.Span).U16();
        if (tents.OwnerOfHouse(c.MapId)?.Character.Furniture.Find(f => f.Index == furn)?.ItemId != Piano)
            return ValueTask.CompletedTask;
        var packet = HouseLifeMessages.FurnitureUsed(session.Account.LoginId, furn, 1);
        foreach (var s in InHouse(session))
            if (s != session)
                s.Send(packet);
        return ValueTask.CompletedTask;
    }

    /// <summary>Handles <c>62/46</c>: the owner sets or clears the doorplate text and everyone near the house is told.</summary>
    /// <param name="session">The owner's session.</param>
    /// <param name="body">The doorplate text; empty clears it.</param>
    internal ValueTask OnDoorplate(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InOwnHouse(session) is not { } c || !c.Furniture.Exists(f => f.ItemId == Doorplate) || body.Length > MaxDoorplateBytes)
        {
            logger.LogInformation("[{Session}] doorplate refused", session.Id);
            return ValueTask.CompletedTask;
        }
        var text = GameEncoding.GetString(body.Span).Trim();
        c.Doorplate = text.Length == 0 ? null : text;
        Broadcast(session, c);
        logger.LogInformation("[{Session}] {Name}'s doorplate: {Text}", session.Id, c.Name, c.Doorplate ?? "(none)");
        return ValueTask.CompletedTask;
    }

    /// <summary>Sends the doorplate, or its removal, to the owner and to the players on the tent's map.</summary>
    /// <param name="session">The owner's session.</param>
    /// <param name="c">The owner's character.</param>
    private void Broadcast(GameSession session, Character c)
    {
        var id = session.Account!.LoginId;
        var packet = c.Doorplate is { } text ? HouseLifeMessages.Doorplate(id, text) : HouseLifeMessages.DoorplateClear(id);
        var map = c.Tent?.MapId;
        foreach (var s in (map is { } m ? world.OnMap(m) : []).Append(session).Distinct())
            s.Send(packet);
    }

    /// <summary>Sends a player the doorplates of the camps shown to them.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="camps">The camps shown.</param>
    private void OnCampsShown(GameSession session, IReadOnlyList<CampRecord> camps)
    {
        foreach (var camp in camps)
            if (tents.OwnerById(camp.OwnerId)?.Character.Doorplate is { } text)
                session.Send(HouseLifeMessages.Doorplate(camp.OwnerId, text));
    }

    /// <summary>Removes the doorplate when the Doorplate piece was taken back.</summary>
    /// <param name="session">The owner's session.</param>
    /// <param name="c">The owner's character.</param>
    private void OnPiecesTakenBack(GameSession session, Character c)
    {
        if (c.Doorplate is not null && !c.Furniture.Exists(f => f.ItemId == Doorplate))
        {
            c.Doorplate = null;
            Broadcast(session, c);
        }
    }
}
