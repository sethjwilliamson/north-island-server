using System.Collections.Frozen;
using Microsoft.Extensions.Logging;
using NorthIsland.GameData.Items;
using NorthIsland.Protocol.IO;
using NorthIsland.Protocol.Messages.Events;
using NorthIsland.Protocol.Messages.Items;
using NorthIsland.Protocol.Messages.World;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.Features.Houses;

/// <summary>
/// Handles tents and the houses inside them: pitching and taking down a tent, entering and leaving a house, the lock, the tent style, and the
/// furniture of the house (placing, moving, taking back, recycling).
/// </summary>
/// <param name="world">The world state, used to find the sessions on a map and to move players between maps.</param>
/// <param name="items">The item data.</param>
/// <param name="store">The account store, used to find the owner of a house who is not online.</param>
/// <param name="space">The space contract, which gives the room size of a house.</param>
/// <param name="options">The house options; the house life section holds the tent limits per map and per server.</param>
/// <param name="logger">The logger.</param>
public sealed class TentHandlers(
    WorldState world,
    ItemData items,
    IAccountStore store,
    SpaceContract space,
    Microsoft.Extensions.Options.IOptions<HouseOptions> options,
    ILogger<TentHandlers> logger)
{
    /// <summary>The item type of tent items.</summary>
    public const byte TentItemType = 27;

    /// <summary>The first map id of the range of house maps; each account has one house map in the range.</summary>
    public const ushort FirstHouseMap = 63501;

    /// <summary>The last map id of the range of house maps.</summary>
    public const ushort LastHouseMap = 65500;

    /// <summary>The room size code of a house whose owner is not known.</summary>
    public const byte TentRoomSize = 1;

    /// <summary>The coordinates where a player appears when they enter a house.</summary>
    public const ushort SpawnX = 540, SpawnY = 780;

    /// <summary>How far below the tent a player is placed on the outside map when they leave the house.</summary>
    public const int ExitOffsetY = 40;

    /// <summary>Returns whether a map is one of the house maps.</summary>
    /// <param name="mapId">The map id.</param>
    /// <returns>True when the map id is in the house map range.</returns>
    public static bool IsHouseMap(int mapId) => mapId is >= FirstHouseMap and <= LastHouseMap;

    /// <summary>Returns the house map of an account; owner ids beyond the range wrap around it.</summary>
    /// <param name="ownerId">The account's login id.</param>
    /// <returns>The map id of the owner's house.</returns>
    public static ushort HouseMapOf(int ownerId) => (ushort)(FirstHouseMap + (Math.Max(ownerId, 1) - 1) % (LastHouseMap - FirstHouseMap + 1));

    /// <summary>Hooks the handler into the world leaving event.</summary>
    internal void Start()
    {
        world.Leaving += OnLeaving;
    }

    /// <summary>Returns whether the item is a door-lock carpet; clicking a placed one opens or closes the house.</summary>
    /// <param name="itemId">The item id of a furniture piece.</param>
    /// <returns>True when it is a door-lock carpet.</returns>
    public static bool IsLockCarpet(ushort itemId) => itemId is 39052 or 39083; // 39052 Small Carpet, 39083 Tiger-fur Carpet

    /// <summary>The tent palette items: item id to the palette set it belongs to and the number of styles in it.</summary>
    private static readonly FrozenDictionary<ushort, (byte Set, byte Styles)> Palettes = new Dictionary<ushort, (byte Set, byte Styles)>
    {
        [34022] = (1, 11), // Tent Palette
        [34160] = (2, 13) // Shape Clay
    }
        .ToFrozenDictionary();

    /// <summary>Handles opening and closing the house (<c>65/3</c> and <c>65/4</c>) by clicking a door-lock carpet in the owner's own house.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    /// <param name="locked">True when the house is to be closed, false when it is to be opened.</param>
    internal ValueTask OnLock(GameSession session, ReadOnlyMemory<byte> body, bool locked)
    {
        if (InOwnHouse(session) is not { } character || body.Length < 2)
            return ValueTask.CompletedTask;
        var index = new PacketReader(body.Span).U16();
        if (character.Tent is not { } tent || !character.Furniture.Exists(f => f.Index == index && IsLockCarpet(f.ItemId)))
        {
            logger.LogWarning("[{Session}] {Name}: lock via furniture {Index}: no carpet or tent", session.Id, character.Name, index);
            return ValueTask.CompletedTask;
        }
        tent.Locked = locked;
        session.Send(HouseMessages.HouseLock(locked, showMessage: true));
        logger.LogInformation("[{Session}] {Name} {What} the house", session.Id, character.Name, locked ? "closes" : "opens");
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Handles using a tent palette from the bag (<c>23/94</c>): answers with the style chooser, or refuses when there is no tent of the right kind.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal static ValueTask OnStyleRequest(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InWorld(session) is not { } character || body.Length < 2)
            return ValueTask.CompletedTask;
        var (slot, set) = (body.Span[0], body.Span[1]);
        if (character.Tent is not { } tent)
            session.Send(HouseMessages.StyleRefused(2));
        else if (tent.ItemId != 36002) // Tent
            session.Send(HouseMessages.StyleRefused(1));
        else
            session.Send(HouseMessages.StyleChooser(slot, set));
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Handles the style the player picked from a tent palette (<c>65/12</c>): the tent gets the style, the palette is used up, and everyone on the
    /// map is told.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal ValueTask OnChangeStyle(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InWorld(session) is not { } character || body.Length < 3)
            return ValueTask.CompletedTask;
        var (style, slot, set) = (body.Span[0], body.Span[1], body.Span[2]);
        var stack = character.Bag.Find(b => b.Slot == slot);
        if (character.Tent is not { } tent || stack is null || !Palettes.TryGetValue(stack.ItemId, out var palette)
            || palette.Set != set || style < 1 || style > palette.Styles || BagLocks.IsLocked(session, slot))
        {
            logger.LogWarning("[{Session}] {Name}: tent style {Style} from bag slot {Slot} refused ({Why})", session.Id, character.Name, style, slot,
                BagLocks.LockedBy(session, slot) ?? "no tent, palette or style");
            return ValueTask.CompletedTask;
        }
        (tent.Kind1, tent.Kind2) = set == 1 ? (style, (byte)0) : ((byte)0, style);
        (character.TentKind1, character.TentKind2) = (tent.Kind1, tent.Kind2);
        stack.Count--;
        if (stack.Count == 0)
            character.Bag.Remove(stack);
        session.Send(ItemMessages.BagSlotDecrease(slot, 1));
        var packet = HouseMessages.StyleChanged(session.Account!.LoginId, tent.Kind1, tent.Kind2);
        foreach (var s in world.OnMap(tent.MapId).Append(session).Distinct())
            s.Send(packet);
        logger.LogInformation("[{Session}] {Name}'s tent gets style {Style} of set {Set}", session.Id, character.Name, style, set);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Handles recycling a placed furniture piece into a recycling bin (<c>62/6</c>): the piece is removed from the house and its recycle product,
    /// if it has one, goes into the bag.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal ValueTask OnRecyclePiece(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InOwnHouse(session) is not { } character || body.Length < 4)
            return ValueTask.CompletedTask;
        var r = new PacketReader(body.Span);
        var (index, binIndex) = (r.U16(), r.U16());
        var piece = character.Furniture.Find(f => f.Index == index);
        var why = piece is null || index == binIndex || piece.ItemId == WorkPlatform || character.Furniture.Exists(f => f.Parent == index)
            || !character.Furniture.Exists(f => f.Index == binIndex && f.ItemId is RecyclingBin or 38059) // Kitty Recycling Bin
            ? "not a piece that can go into this bin"
            : LeaveRefusal(character, [piece]);
        if (why is not null || piece is null)
        {
            logger.LogWarning("[{Session}] {Name}: recycle furniture {Index}: {Why}", session.Id, character.Name, index, why);
            return ValueTask.CompletedTask;
        }
        character.Furniture.Remove(piece);
        SendToHouse(session, HouseMessages.FurnitureRemoved(index));
        if (piece.ItemId == FurMailHandlers.Mailbox)
            FurMailHandlers.SendState(session, character);
        if (IsLockCarpet(piece.ItemId) && character.Tent is { } tent && !character.Furniture.Exists(f => IsLockCarpet(f.ItemId)))
            tent.Locked = false;
        GiveRecycleProduct(session, character, piece.ItemId);
        return ValueTask.CompletedTask;
    }

    /// <summary>Puts the recycle product of an item into the bag, or logs that the item is destroyed when it has none.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's active character.</param>
    /// <param name="itemId">The item id that was recycled.</param>
    private void GiveRecycleProduct(GameSession session, Character character, ushort itemId)
    {
        var product = items[itemId] is { } record ? items.RecycleProduct(record) : null;
        if (product is null)
        {
            logger.LogInformation("[{Session}] {Name} recycles {Item}: destroyed", session.Id, character.Name, NameOf(itemId));
            return;
        }
        foreach (var (stack, added) in Bag.Add(character, items, product.Id, 1))
            session.Send(ItemMessages.BagAdd(new BagEntry { Slot = stack.Slot, ItemId = stack.ItemId, Count = added, Wear = stack.Wear }));
        logger.LogInformation("[{Session}] {Name} recycles {Item} into {Product}", session.Id, character.Name, NameOf(itemId), product.Name);
    }

    /// <summary>The item id of the Work Platform, the facility on which tools are put.</summary>
    public const ushort WorkPlatform = 38049; // Work Platform

    /// <summary>The item id of the Recycling Bin furniture.</summary>
    public const ushort RecyclingBin = 38055; // Resource Recycling

    /// <summary>The item types of house pieces: accessories and small tools (28), instruments (29) and furniture (30).</summary>
    public const byte AccessoryType = 28, InstrumentType = 29, FurnitureType = 30;

    /// <summary>
    /// Whether an item can stand in a house as a piece of furniture, by the client's rule (CheckHomeDiscard, "placeable"): a furniture item that
    /// is not a floor or wallpaper, or a large (more than one bag cell) instrument or accessory, with a furniture size on all three axes
    /// (<see cref="ItemRecord.FurnitureSize"/>). Anything else, such as a vehicle, a tent or equipment, cannot be placed.
    /// </summary>
    /// <param name="r">The item's Item.dat record, null for an unknown item.</param>
    /// <returns>True when the item can be placed as furniture.</returns>
    public static bool IsPlaceable(ItemRecord? r) =>
        r is { FurnitureSize: [> 0, > 0, > 0] }
            && (r.Type == FurnitureType ? !HouseDecorHandlers.IsFloor(r.Id) && !HouseDecorHandlers.IsWallpaper(r.Id)
                : r.Type is AccessoryType or InstrumentType && (r.CellWidth != 1 || r.CellHeight != 1));

    /// <summary>Whether an item can be put on a work platform as a tool, by the client's rule: an instrument of one bag cell.</summary>
    /// <param name="r">The item's Item.dat record, null for an unknown item.</param>
    /// <returns>True when the item is a tool.</returns>
    public static bool IsTool(ItemRecord? r) => r is { Type: InstrumentType, CellWidth: 1, CellHeight: 1 };

    /// <summary>
    /// Returns why furniture pieces cannot leave the house (be taken back or recycled), or null when they can: a rented room door, a kept piece,
    /// a piece that still holds items, sleeping pets, offers or a stall case.
    /// </summary>
    /// <param name="character">The house owner's character.</param>
    /// <param name="pieces">The pieces leaving, a piece with the tools on it.</param>
    /// <returns>The reason as log text; null when the pieces may leave.</returns>
    private static string? LeaveRefusal(Character character, List<Furniture> pieces) =>
        pieces.Exists(p => p.RentStartUtc is not null || p.Retained) ? "rented out or kept"
            : pieces.Exists(p => p.Contents.Count > 0 || p.Sleepers.Count > 0 || p.Stock.Count > 0)
                || character.StallCases.Exists(x => pieces.Exists(p => p.Index == x.FurnIndex)) ? "still holds items"
                : null;

    /// <summary>Gives a house that was never furnished its two default pieces, a Work Platform and a Recycling Bin.</summary>
    /// <param name="owner">The owner's character.</param>
    private static void EnsureDefaultFurniture(Character owner)
    {
        if (owner.HouseFurnished)
            return;
        owner.HouseFurnished = true;
        foreach (var (item, x, y) in new[] { (WorkPlatform, 32, 39), (RecyclingBin, 41, 39) })
        {
            if (!owner.Furniture.Exists(f => f.ItemId == item) && FreeIndex(owner) is var index and not 0)
                owner.Furniture.Add(new Furniture { Index = index, ItemId = item, X = x, Y = y, Z = 1 });
        }
    }

    /// <summary>Finds the owner of a house or tent by account id, from the online session or else from the account store.</summary>
    /// <param name="ownerId">The account's login id.</param>
    /// <returns>The owner; null when no account has that id.</returns>
    public TentOwner? OwnerById(int ownerId)
    {
        if (world.SessionOf(ownerId) is { } s && s.ActiveCharacter is { } active)
            return new TentOwner(ownerId, active, s);
        var account = store.All().FirstOrDefault(a => a.LoginId == ownerId);
        var character = account?.Characters.Find(c => c.Tent is not null) ?? account?.Characters.FirstOrDefault();
        return character is null ? null : new TentOwner(ownerId, character, null);
    }

    /// <summary>
    /// Finds the owner of the house on a house map. House maps wrap around every 2000 accounts, so several accounts can share one; the online
    /// account whose tent is pitched (the one whose house is open, see <see cref="SharerInUse"/>) is the owner then, otherwise the first account.
    /// </summary>
    /// <param name="houseMapId">The house map id.</param>
    /// <returns>The owner; null when no account has that house.</returns>
    public TentOwner? OwnerOfHouse(int houseMapId)
    {
        if (world.OnlineSessions().FirstOrDefault(s => s.Account is { } a && HouseMapOf(a.LoginId) == houseMapId && s.ActiveCharacter?.Tent is not null)
            is { Account: { } host })
            return OwnerById(host.LoginId);
        return store.All().FirstOrDefault(a => HouseMapOf(a.LoginId) == houseMapId) is { } account ? OwnerById(account.LoginId) : null;
    }

    /// <summary>
    /// Finds another online account that shares an account's house map (house maps wrap around every 2000 accounts) and has its house in use:
    /// its tent is pitched or it stands on the map. Only one of the accounts sharing a map may use it at a time, so that nobody is put into, or
    /// given owner rights in, the house of another account.
    /// </summary>
    /// <param name="ownerId">The account's login id.</param>
    /// <returns>The other account's session; null when no other account uses the map.</returns>
    public GameSession? SharerInUse(int ownerId)
    {
        var map = HouseMapOf(ownerId);
        return world.OnlineSessions().FirstOrDefault(s => s.Account is { } a && a.LoginId != ownerId && HouseMapOf(a.LoginId) == map
            && s.ActiveCharacter is { } c && (c.Tent is not null || c.MapId == map));
    }

    /// <summary>Finds the owner of the house a character stands in, when it is the given account.</summary>
    /// <param name="visitor">The character in the house.</param>
    /// <param name="ownerId">The login id of the owner the client named.</param>
    /// <returns>The owner; null when the character is not in that account's house.</returns>
    public TentOwner? HouseOwnerFor(Character visitor, int ownerId) =>
        IsHouseMap(visitor.MapId) && HouseMapOf(ownerId) == visitor.MapId && OwnerOfHouse(visitor.MapId) is { } owner && owner.Id == ownerId ? owner : null;

    /// <summary>Lists the camps of every online player who has a tent pitched.</summary>
    /// <returns>The camp records.</returns>
    private IEnumerable<CampRecord> AllCamps() =>
        world.OnlineSessions().Where(s => s.Account is not null && s.ActiveCharacter?.Tent is not null)
            .Select(s => CampOf(s.Account!.LoginId, s.ActiveCharacter!.Tent!));

    /// <summary>
    /// Runs when a player leaves the world: a character inside a house is put at the tent's exit, and a pitched tent is taken down.
    /// </summary>
    /// <param name="session">The player's session.</param>
    private void OnLeaving(GameSession session)
    {
        if (session.ActiveCharacter is not { } character || session.Account is null)
            return;
        if (IsHouseMap(character.MapId))
        {
            var exit = OwnerOfHouse(character.MapId)?.Character.Tent is { } houseTent ? ExitOf(houseTent) : WorldHandlers.StartersBeach;
            (character.MapId, character.X, character.Y) = (exit.MapId, exit.X, exit.Y);
        }
        if (character.Tent is not null)
            TakeDown(new TentOwner(session.Account.LoginId, character, null));
    }

    /// <summary>
    /// Pitches a tent from a bag stack (<c>23/15</c>) at the character's position and tells everyone on the map. It is refused in a house, in a
    /// battle, while riding, while another online account that shares the house map uses it (<see cref="SharerInUse"/>), and when the tent
    /// limits of the map or server are reached; the player's other tent is taken down first.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="stack">The tent item in the bag.</param>
    public void Pitch(GameSession session, BagItem stack)
    {
        var character = session.ActiveCharacter!;
        if (IsHouseMap(character.MapId) || session.Battle is not null || session.Vehicle is { Riding: true })
        {
            logger.LogInformation("[{Session}] {Name}: cannot pitch a tent here", session.Id, character.Name);
            return;
        }
        var ownerId = session.Account!.LoginId;
        if (SharerInUse(ownerId) is { } sharer)
        {
            logger.LogWarning("[{Session}] {Name}: cannot pitch a tent: house map {Map} is in use by account {Other}", session.Id, character.Name,
                HouseMapOf(ownerId), sharer.Account!.LoginId);
            return;
        }
        var (perMap, perServer) = (options.Value.HouseLife.TentsPerMap, options.Value.HouseLife.TentsPerServer);
        var others = AllCamps().Where(c => c.OwnerId != ownerId).ToList();
        if (perServer > 0 && others.Count >= perServer)
        {
            session.Send(new PacketWriter(Action65.Code, Action65.ServerLimit).ToArray());
            return;
        }
        if (perMap > 0 && others.Count(c => TentMap(c.OwnerId) == character.MapId) >= perMap)
        {
            session.Send(new PacketWriter(Action65.Code, Action65.SceneLimit).ToArray());
            return;
        }
        foreach (var c in session.Account.Characters.Where(c => c.Tent is not null))
            TakeDown(new TentOwner(ownerId, c, c == character ? session : null));
        character.Tent = new PitchedTent
        {
            MapId = character.MapId, X = character.X, Y = character.Y, ItemId = stack.ItemId,
            Kind1 = character.TentKind1, Kind2 = character.TentKind2,
        };
        var packet = HouseMessages.CampAdded(CampOf(ownerId, character.Tent));
        foreach (var s in world.OnMap(character.MapId).Append(session).Distinct())
            s.Send(packet);
        logger.LogInformation("[{Session}] {Name} pitches {Item} on map {Map} at ({X}, {Y})", session.Id, character.Name,
            NameOf(stack.ItemId), character.MapId, character.X, character.Y);
    }

    /// <summary>
    /// Sends what a player needs on entering a map: in a house, the furniture list, the lock state and the house-entered event; elsewhere, the
    /// camps pitched on the map.
    /// </summary>
    /// <param name="session">The player's session.</param>
    public void MapEntered(GameSession session)
    {
        var map = session.ActiveCharacter!.MapId;
        if (IsHouseMap(map))
        {
            var owner = OwnerOfHouse(map);
            if (owner is not null)
                EnsureDefaultFurniture(owner.Character);
            session.Send(HouseMessages.FurnitureList(owner?.Id ?? 0,
                owner is { Character: var oc } ? oc.Furniture.Select(f => (f.Index, RecordOf(f, oc))) : []));
            if (owner?.Session == session)
                session.Send(HouseMessages.HouseLock(owner.Character.Tent?.Locked == true, showMessage: false));
            HouseEntered?.Invoke(session, owner?.Session);
            return;
        }
        var camps = AllCamps().Where(c => TentMap(c.OwnerId) == map).ToList();
        if (camps.Count > 0)
        {
            session.Send(HouseMessages.CampList(camps));
            CampsShown?.Invoke(session, camps);
        }
    }

    /// <summary>Returns the map a player's tent is pitched on.</summary>
    /// <param name="ownerId">The account's login id.</param>
    /// <returns>The map id; null when the owner has no tent.</returns>
    private ushort? TentMap(int ownerId) => OwnerById(ownerId)?.Character.Tent?.MapId;

    /// <summary>Called with the player and the camps on the map when a map's camps have been sent to a player.</summary>
    public Action<GameSession, IReadOnlyList<CampRecord>>? CampsShown { get; set; }

    /// <summary>Called with the player and their character when furniture pieces were taken back into the bag.</summary>
    public Action<GameSession, Character>? PiecesTakenBack { get; set; }

    /// <summary>Called when a player has entered a house, with the player and the house owner's session (null when the owner is not online).</summary>
    public Action<GameSession, GameSession?>? HouseEntered { get; set; }

    /// <summary>Moves a character that is saved inside a house to the exit of the tent it belongs to, or to the starters' beach.</summary>
    /// <param name="character">The character being placed.</param>
    /// <param name="ownerId">The account's login id.</param>
    public void FixLoginPosition(Character character, int ownerId)
    {
        if (!IsHouseMap(character.MapId))
            return;
        var tent = HouseMapOf(ownerId) == character.MapId ? character.Tent : OwnerOfHouse(character.MapId)?.Character.Tent;
        var exit = tent is null ? WorldHandlers.StartersBeach : ExitOf(tent);
        (character.MapId, character.X, character.Y) = (exit.MapId, exit.X, exit.Y);
    }

    /// <summary>Fixes the saved position of a character that logs in, and clears its tent: tents do not survive a login.</summary>
    /// <param name="character">The character logging in.</param>
    /// <param name="ownerId">The account's login id.</param>
    public void OnLogin(Character character, int ownerId)
    {
        FixLoginPosition(character, ownerId);
        character.Tent = null;
    }

    /// <summary>
    /// Handles clicking a tent to go in (<c>65/1</c>): the player enters the house unless it is closed to them, they stand farther from the tent
    /// than the client's screen reaches (<see cref="HouseLifeOptions.TentReachX"/>, <see cref="HouseLifeOptions.TentReachY"/>), or the house map
    /// is shared with the player's own house or with another account that uses it (house maps wrap around every 2000 accounts).
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal ValueTask OnEnter(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InWorld(session) is not { } character || body.Length < 4)
            return ValueTask.CompletedTask;
        var ownerId = new PacketReader(body.Span).I32();
        var owner = OwnerById(ownerId);
        var tent = owner?.Character.Tent;
        var (reachX, reachY) = (options.Value.HouseLife.TentReachX, options.Value.HouseLife.TentReachY);
        if (tent is null || tent.MapId != character.MapId)
            logger.LogInformation("[{Session}] {Name}: no tent of {Owner} here", session.Id, character.Name, ownerId);
        else if ((reachX > 0 && Math.Abs(tent.X - character.X) > reachX) || (reachY > 0 && Math.Abs(tent.Y - character.Y) > reachY))
        {
            logger.LogWarning("[{Session}] {Name}: tent of {Owner} at ({TentX}, {TentY}) is out of reach of ({X}, {Y})", session.Id, character.Name,
                ownerId, tent.X, tent.Y, character.X, character.Y);
            session.Send(HouseMessages.EnterForbidden());
        }
        else if ((ownerId != session.Account!.LoginId && HouseMapOf(session.Account.LoginId) == HouseMapOf(ownerId)) || SharerInUse(ownerId) is not null)
        {
            logger.LogWarning("[{Session}] {Name}: house map {Map} of {Owner} is shared with another account in use", session.Id, character.Name,
                HouseMapOf(ownerId), ownerId);
            session.Send(HouseMessages.EnterForbidden());
        }
        else if (tent.Locked && owner!.Session != session)
            session.Send(HouseMessages.EnterForbidden());
        else
        {
            logger.LogInformation("[{Session}] {Name} enters the tent of {Owner}", session.Id, character.Name, owner!.Character.Name);
            world.ChangeMap(session, new DoorWarp(HouseMapOf(ownerId), SpawnX, SpawnY, 0, space.TentSize(owner!.Character)));
        }
        session.Send(EventMessages.Finished());
        return ValueTask.CompletedTask;
    }

    /// <summary>Returns the room size code of a house.</summary>
    /// <param name="houseMap">The house map id.</param>
    /// <returns>The size from the owner's space contract; the default tent size when the owner is not known.</returns>
    public byte RoomSizeOf(int houseMap) => OwnerOfHouse(houseMap) is { } owner ? space.TentSize(owner.Character) : TentRoomSize;

    /// <summary>Takes the player out through the door of a house: from a rented room back to the house, otherwise out to the tent's exit.</summary>
    /// <param name="session">The player's session.</param>
    public void LeaveHouse(GameSession session)
    {
        var character = session.ActiveCharacter!;
        if (session.HouseRoom > 1)
        {
            world.ChangeMap(session, new DoorWarp(character.MapId, SpawnX, SpawnY, 0, RoomSizeOf(character.MapId)));
            session.Send(EventMessages.Finished());
            return;
        }
        var tent = OwnerOfHouse(character.MapId)?.Character.Tent;
        world.ChangeMap(session, tent is null ? WorldHandlers.StartersBeach : ExitOf(tent) with { ConveyIndex = 1 });
        session.Send(EventMessages.Finished());
    }

    /// <summary>Handles taking down the player's own tent by using it from the bag (<c>65/2</c>).</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal ValueTask OnTakeDown(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InWorld(session) is not { } character || body.Length < 1)
            return ValueTask.CompletedTask;
        var stack = character.Bag.Find(b => b.Slot == body.Span[0]);
        if (character.Tent is null || stack?.ItemId != character.Tent.ItemId || IsHouseMap(character.MapId))
        {
            logger.LogWarning("[{Session}] {Name}: take down tent from bag slot {Slot}: no pitched tent", session.Id, character.Name, body.Span[0]);
            return ValueTask.CompletedTask;
        }
        TakeDown(new TentOwner(session.Account!.LoginId, character, session));
        return ValueTask.CompletedTask;
    }

    /// <summary>Takes a tent down: tells the map, sends every visitor out of the house, and clears the tent.</summary>
    /// <param name="owner">The owner whose tent comes down.</param>
    private void TakeDown(TentOwner owner)
    {
        if (owner.Character.Tent is not { } tent)
            return;
        var packet = HouseMessages.CampTakenBack(owner.Id);
        var receivers = world.OnMap(tent.MapId);
        if (owner.Session is { } own)
            receivers = receivers.Append(own);
        foreach (var s in receivers.Distinct())
            s.Send(packet);
        var exit = ExitOf(tent) with { ConveyIndex = 1 };
        foreach (var visitor in world.OnMap(HouseMapOf(owner.Id)).ToList())
        {
            if (visitor != owner.Session)
                visitor.Send(HouseMessages.HostLeft());
            world.ChangeMap(visitor, exit);
        }
        owner.Character.Tent = null;
        logger.LogInformation("{Name}'s tent on map {Map} comes down", owner.Character.Name, tent.MapId);
    }

    /// <summary>Handles placing a bag item as furniture in the player's own house (<c>62/1</c>).</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal ValueTask OnPlace(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InOwnHouse(session) is not { } character || body.Length < 14)
            return ValueTask.CompletedTask;
        var r = new PacketReader(body.Span);
        var (slot, x, y, z, dir) = (r.U8(), r.I32(), r.I32(), r.I32(), r.U8());
        if (session.HouseRoom > 1 || TakeOne(session, character, slot, IsPlaceable, "place furniture") is not { } itemId)
            return ValueTask.CompletedTask;
        var piece = new Furniture { Index = FreeIndex(character), ItemId = itemId, X = x, Y = y, Z = z, Dir = dir, Room = session.HouseRoom };
        character.Furniture.Add(piece);
        SendToHouse(session, HouseMessages.FurnitureAdded(piece.Index, RecordOf(piece, character)));
        if (itemId == FurMailHandlers.Mailbox)
            FurMailHandlers.SendState(session, character);
        logger.LogInformation("[{Session}] {Name} places {Item} at ({X}, {Y}) as furniture {Index}", session.Id, character.Name,
            NameOf(itemId), x, y, piece.Index);
        return ValueTask.CompletedTask;
    }

    /// <summary>Handles putting a tool from the bag on a work platform (<c>62/4</c>).</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal ValueTask OnPlaceTool(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InOwnHouse(session) is not { } character || body.Length < 3)
            return ValueTask.CompletedTask;
        var r = new PacketReader(body.Span);
        var (slot, platformIndex) = (r.U8(), r.U16());
        var platform = character.Furniture.Find(f => f.Index == platformIndex && f.Parent == 0);
        var stack = character.Bag.Find(b => b.Slot == slot);
        var tools = character.Furniture.Where(f => f.Parent == platformIndex).ToList();
        if (platform is null || platform.ItemId != WorkPlatform || stack is null || tools.Count >= Furniture.MaxToolsPerPlatform
            || tools.Exists(t => t.ItemId == stack.ItemId))
        {
            logger.LogWarning("[{Session}] {Name}: tool from bag slot {Slot} cannot go on furniture {Index}", session.Id, character.Name, slot, platformIndex);
            return ValueTask.CompletedTask;
        }
        if (TakeOne(session, character, slot, IsTool, "place tool") is not { } itemId)
            return ValueTask.CompletedTask;
        var tool = new Furniture
        {
            Index = FreeIndex(character),
            ItemId = itemId,
            X = platform.X,
            Y = platform.Y,
            Z = platform.Z,
            Room = platform.Room,
            Parent = platformIndex
        };
        character.Furniture.Add(tool);
        SendToHouse(session, HouseMessages.ToolLinked(tool.Index, itemId, platformIndex));
        logger.LogInformation("[{Session}] {Name} puts {Item} on furniture {Platform} as {Index}", session.Id, character.Name,
            NameOf(itemId), platformIndex, tool.Index);
        return ValueTask.CompletedTask;
    }

    /// <summary>Handles recycling a bag item at a recycling bin (<c>62/5</c>): the items are used up and the recycle product goes into the bag.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal ValueTask OnRecycle(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InOwnHouse(session) is not { } character || body.Length < 4)
            return ValueTask.CompletedTask;
        var r = new PacketReader(body.Span);
        var (slot, count, binIndex) = (r.U8(), r.U8(), r.U16());
        var bin = character.Furniture.Find(f => f.Index == binIndex && f.ItemId is RecyclingBin or 38059); // Kitty Recycling Bin
        var stack = character.Bag.Find(b => b.Slot == slot);
        if (bin is null || stack is null || stack.ItemId == character.Tent?.ItemId || BagLocks.IsLocked(session, slot))
        {
            logger.LogWarning("[{Session}] {Name}: recycle bag slot {Slot} at furniture {Bin}: refused ({Why})", session.Id, character.Name, slot,
                binIndex, BagLocks.LockedBy(session, slot) ?? "no bin, an empty slot or the pitched tent");
            return ValueTask.CompletedTask;
        }
        var taken = count == 0 || count > stack.Count ? stack.Count : count;
        stack.Count -= taken;
        if (stack.Count == 0)
            character.Bag.Remove(stack);
        session.Send(ItemMessages.BagSlotDecrease(slot, taken));
        GiveRecycleProduct(session, character, stack.ItemId);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Handles taking a furniture piece, with the tools on it, back into the bag (<c>62/2</c>). It is refused when the bag has no room, when a piece
    /// is rented out, kept or still holds items (<see cref="LeaveRefusal"/>), is not a furniture item or tool, or is a Magic Sandglass with uses
    /// spent: the uses are kept on the placed piece only, so a new piece placed from the bag would start with all of them again.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal ValueTask OnTakeBack(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InOwnHouse(session) is not { } character || body.Length < 2)
            return ValueTask.CompletedTask;
        var index = new PacketReader(body.Span).U16();
        var piece = character.Furniture.Find(f => f.Index == index);
        if (piece is null)
            return ValueTask.CompletedTask;
        var pieces = character.Furniture.Where(f => f.Parent == index).Append(piece).ToList();
        var why = LeaveRefusal(character, pieces)
            ?? (!pieces.TrueForAll(p => p.Parent == 0 ? IsPlaceable(items[p.ItemId]) : IsTool(items[p.ItemId])) ? "not a furniture item"
                : pieces.Exists(p => ManufactureHandlers.IsSandglass(p.ItemId) && p.Uses > 0) ? "a Magic Sandglass with uses spent"
                    : null);
        if (why is not null)
        {
            logger.LogWarning("[{Session}] {Name}: take back furniture {Index}: {Why}", session.Id, character.Name, index, why);
            return ValueTask.CompletedTask;
        }
        var trial = character.Bag.Select(
            b => new BagItem { Slot = b.Slot, ItemId = b.ItemId, Count = b.Count, Wear = b.Wear, Fusion = b.Fusion, Pose = b.Pose, BoxItem = b.BoxItem })
            .ToList();
        if (!pieces.TrueForAll(p => Bag.Add(trial, items, p.ItemId, 1).Count == 1))
        {
            logger.LogInformation("[{Session}] {Name}: no room in the bag for furniture {Index}", session.Id, character.Name, index);
            return ValueTask.CompletedTask;
        }
        if (pieces.Exists(p => IsLockCarpet(p.ItemId)) && character.Tent is { } tent
            && !character.Furniture.Except(pieces).Any(f => IsLockCarpet(f.ItemId)))
            tent.Locked = false;
        foreach (var p in pieces)
        {
            character.Furniture.Remove(p);
            SendToHouse(session, HouseMessages.FurnitureRemoved(p.Index));
            foreach (var (stack, added) in Bag.Add(character, items, p.ItemId, 1))
                session.Send(ItemMessages.BagAdd(new BagEntry { Slot = stack.Slot, ItemId = stack.ItemId, Count = added, Wear = stack.Wear }));
        }
        if (pieces.Exists(p => p.ItemId == FurMailHandlers.Mailbox))
            FurMailHandlers.SendState(session, character);
        PiecesTakenBack?.Invoke(session, character);
        logger.LogInformation("[{Session}] {Name} takes back furniture {Index} ({Count} piece(s))", session.Id, character.Name, index, pieces.Count);
        return ValueTask.CompletedTask;
    }

    /// <summary>Handles moving a placed furniture piece, and the tools on it, to a new position (<c>62/3</c>).</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal ValueTask OnMove(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InOwnHouse(session) is not { } character || body.Length < 15)
            return ValueTask.CompletedTask;
        var r = new PacketReader(body.Span);
        var (index, x, y, z, dir) = (r.U16(), r.I32(), r.I32(), r.I32(), r.U8());
        if (character.Furniture.Find(f => f.Index == index && f.Parent == 0) is not { } piece || session.HouseRoom > 1)
            return ValueTask.CompletedTask;
        if (piece.Retained)
        {
            (piece.X, piece.Y, piece.Z, piece.Dir, piece.Room, piece.Retained) = (x, y, z, dir, session.HouseRoom, false);
            SendToHouse(session, HouseMessages.FurniturePlaced(index, x, y, z, dir, piece.Room));
            return ValueTask.CompletedTask;
        }
        (piece.X, piece.Y, piece.Z, piece.Dir) = (x, y, z, dir);
        foreach (var tool in character.Furniture.Where(f => f.Parent == index))
            (tool.X, tool.Y, tool.Z) = (x, y, z);
        SendToHouse(session, HouseMessages.FurnitureMoved(index, x, y, z, dir));
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Takes one item out of a bag stack to be placed as furniture or a tool; an item of the wrong kind, a locked bag slot (<see cref="BagLocks"/>:
    /// in a trade, a stall, a house shelf, the pet express or the ridden vehicle) and a full house are refused.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's active character.</param>
    /// <param name="slot">The bag slot.</param>
    /// <param name="fits">Whether the item is of the kind wanted (<see cref="IsPlaceable"/> or <see cref="IsTool"/>).</param>
    /// <param name="what">What the item is for, used in the log.</param>
    /// <returns>The item id taken; null when the item cannot be used.</returns>
    private ushort? TakeOne(GameSession session, Character character, byte slot, Func<ItemRecord?, bool> fits, string what)
    {
        var stack = character.Bag.Find(b => b.Slot == slot);
        var why = stack is null ? "it is empty"
            : !fits(items[stack.ItemId]) ? "the item is of the wrong kind"
                : character.Furniture.Count >= Furniture.MaxPieces ? "the house is full"
                    : BagLocks.LockedBy(session, slot) is { } locked ? $"it is {locked}"
                        : null;
        if (stack is null || why is not null)
        {
            logger.LogWarning("[{Session}] {Name}: {What}: bag slot {Slot} cannot be used: {Why}", session.Id, character.Name, what, slot, why);
            return null;
        }
        stack.Count--;
        if (stack.Count == 0)
            character.Bag.Remove(stack);
        session.Send(ItemMessages.BagSlotDecrease(slot, 1));
        return stack.ItemId;
    }

    /// <summary>Returns the name of an item for the log, or <c>item N</c> when it is unknown.</summary>
    /// <param name="id">The item id.</param>
    /// <returns>The item name.</returns>
    private string NameOf(ushort id) => items[id]?.Name ?? $"item {id}";

    /// <summary>Returns the lowest furniture index not used by the character's house.</summary>
    /// <param name="character">The house owner's character.</param>
    /// <returns>The free index; 0 when the house is full.</returns>
    internal static ushort FreeIndex(Character character)
    {
        for (ushort i = 1; i <= Furniture.MaxPieces; i++)
            if (!character.Furniture.Exists(f => f.Index == i))
                return i;
        return 0;
    }

    /// <summary>Sends a packet to everyone in the house the player is in, and to the player.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="packet">The encoded packet.</param>
    private void SendToHouse(GameSession session, byte[] packet)
    {
        foreach (var s in world.OnMap(session.ActiveCharacter!.MapId).Append(session).Distinct())
            s.Send(packet);
    }

    /// <summary>
    /// Builds the furniture record the client is sent, including the indexes of the tools on a piece. A tool on a work platform is sent with no
    /// cell (0, 0, 0), as the tool-linked packet sends it: the client checks a placed record's last cell (x - 1, y - 1 for a tool, which has no
    /// furniture size), so a tool sent with the cell of a platform against the left or back wall is refused and drops off the platform.
    /// </summary>
    /// <param name="f">The furniture piece.</param>
    /// <param name="owner">The house owner's character.</param>
    /// <returns>The record.</returns>
    public static FurnitureRecord RecordOf(Furniture f, Character owner) =>
        f.Parent != 0
            ? new(f.ItemId, 0, 0, 0, f.Dir, f.Parent, [], f.Room, f.Retained)
            : new(f.ItemId, f.X, f.Y, f.Z, f.Dir, f.Parent,
                [.. owner.Furniture.Where(t => t.Parent == f.Index).OrderBy(t => t.Index).Select(t => t.Index)], f.Room, f.Retained);

    /// <summary>Builds the camp record the client is sent for a pitched tent.</summary>
    /// <param name="ownerId">The tent owner's login id.</param>
    /// <param name="t">The pitched tent.</param>
    /// <returns>The record.</returns>
    private static CampRecord CampOf(int ownerId, PitchedTent t) => new(ownerId, t.ItemId, t.X, t.Y, t.Kind1, t.Kind2);

    /// <summary>Returns the door warp that leads out of a house to just below its tent.</summary>
    /// <param name="tent">The pitched tent.</param>
    /// <returns>The warp to the tent's map.</returns>
    private static DoorWarp ExitOf(PitchedTent tent) =>
        new(tent.MapId, (ushort)Math.Clamp(tent.X, 0, ushort.MaxValue), (ushort)Math.Clamp(tent.Y + ExitOffsetY, 0, ushort.MaxValue), 0);

    /// <summary>Returns the active character of a session that is in the world and not in a battle.</summary>
    /// <param name="session">The player's session.</param>
    /// <returns>The character; null when the session is not in the world or is in a battle.</returns>
    private static Character? InWorld(GameSession session)
    {
        var character = session.ActiveCharacter;
        return session.State == SessionState.InWorld && character is not null && session.Account is not null && session.Battle is null ? character : null;
    }

    /// <summary>Returns the active character of a session that stands in its own house.</summary>
    /// <param name="session">The player's session.</param>
    /// <returns>The character; null when the session is not in the world, is in a battle or is not in its own house.</returns>
    private static Character? InOwnHouse(GameSession session) =>
        InWorld(session) is { } character && HouseMapOf(session.Account!.LoginId) == character.MapId ? character : null;
}
