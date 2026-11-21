using System.Collections.Concurrent;
using NorthIsland.Protocol.Messages.Items;

namespace NorthIsland.Server.Features.Storage;

/// <summary>The service windows the server opens for a player and then serves the requests of.</summary>
public enum ServiceWindow
{
    /// <summary>The bank's item window: stores and takes stacks (<c>30/x</c>) and sorts the bank (<c>23/250</c>).</summary>
    BankItems,

    /// <summary>The bank's money window: pays gold in and takes it out (<c>29/1</c>, <c>29/2</c>).</summary>
    BankMoney,

    /// <summary>The ATM deposit form: pays gold in and takes it out (<c>29/3</c>, <c>29/4</c>).</summary>
    AtmDeposit,

    /// <summary>The ATM transfer form: sends ATM gold to another player (<c>29/5</c>).</summary>
    AtmTransfer,

    /// <summary>The point exchange form: exchanges mall points for gold (<c>23/109</c>).</summary>
    PointExchange,

    /// <summary>The window for selling equipment to an NPC (<c>27/2</c> with sale kind 0).</summary>
    SellEquipment,

    /// <summary>The window for selling goods to an NPC (<c>27/2</c> with sale kind 1).</summary>
    SellGoods,

    /// <summary>An NPC store dialog of the map, by its interact index (<c>27/1</c>).</summary>
    Store,

    /// <summary>A point store of an NPC event, with its rows (<c>80/1</c>).</summary>
    PointStore,

    /// <summary>The pet storage of the inn or of a Wit Maid (<c>31/x</c>).</summary>
    PetStorage,

    /// <summary>
    /// The Item Bag, opened by the warehouse service or by using a Space Contract (<c>68/x</c>) and sorted by <c>23/250</c>. A Space Contract opens
    /// it anywhere and the client keeps it open across map changes, so unlike the other windows it is not bound to a map.
    /// </summary>
    ItemBag,
}

/// <summary>A service window the server opened: for which character, on which map, and what it offers.</summary>
/// <param name="Character">The character it was opened for.</param>
/// <param name="MapId">The map it was opened on.</param>
/// <param name="Interact">The interact index of a store dialog on the map; 0 for any other window.</param>
/// <param name="Rows">The rows of a point store; empty for any other window.</param>
public sealed record OpenService(Character Character, int MapId, int Interact, IReadOnlyList<PointStoreRow> Rows);

/// <summary>The service windows open on one session, by kind.</summary>
public sealed class OpenServicesSessionState
{
    /// <summary>The open windows; a window opened again replaces the record of its kind.</summary>
    public ConcurrentDictionary<ServiceWindow, OpenService> Windows { get; } = new();
}

/// <summary>
/// Keeps the service windows the server opened for each session, so a request of a window is served only while that window is open. A window
/// stays open after the NPC event that opened it ends (a click on the world while it is shown ends the event) and closes when the player
/// leaves the map, except the Item Bag, which is not bound to a map (<see cref="ServiceWindow.ItemBag"/>); no window is served in a battle, nor
/// for another character of the session.
/// </summary>
/// <param name="world">The world state, whose map changes close the windows.</param>
public sealed class OpenServices(
    WorldState world)
{
    /// <summary>Closes the map-bound service windows of a player who leaves their map.</summary>
    internal void Start() => world.MapLeaving += CloseMapBound;

    /// <summary>Records a service window opened for the player's active character on their current map.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's active character.</param>
    /// <param name="window">The kind of window.</param>
    /// <param name="mapId">The map the window belongs to; null for the character's current map.</param>
    /// <param name="interact">The interact index of a store dialog; 0 for any other window.</param>
    /// <param name="rows">The rows of a point store; null for any other window.</param>
    public static void Open(
        GameSession session,
        Character character,
        ServiceWindow window,
        int? mapId = null,
        int interact = 0,
        IEnumerable<PointStoreRow>? rows = null) =>
        StateOf(session).Windows[window] = new OpenService(character, mapId ?? character.MapId, interact, rows is null ? [] : [.. rows]);

    /// <summary>
    /// Finds an open service window the player may use now: opened for this character, out of battle, and on their current map unless the window
    /// is not bound to a map (<see cref="IsMapBound"/>).
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's active character.</param>
    /// <param name="window">The kind of window.</param>
    /// <returns>The open window, or null when the request must be refused.</returns>
    public static OpenService? Of(GameSession session, Character character, ServiceWindow window) =>
        session.Battle is null && StateOf(session).Windows.TryGetValue(window, out var open) && ReferenceEquals(open.Character, character)
            && (!IsMapBound(window) || open.MapId == character.MapId)
            ? open
            : null;

    /// <summary>Whether the player may use a service window now (<see cref="Of"/>).</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's active character.</param>
    /// <param name="window">The kind of window.</param>
    /// <returns>True when the request may be served.</returns>
    public static bool IsOpen(GameSession session, Character character, ServiceWindow window) => Of(session, character, window) is not null;

    /// <summary>The map a window of the kind was last opened on, for the log of a refusal.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="window">The kind of window.</param>
    /// <returns>The map id, or null when no window of the kind is recorded.</returns>
    public static int? MapOf(GameSession session, ServiceWindow window) =>
        StateOf(session).Windows.TryGetValue(window, out var open) ? open.MapId : null;

    /// <summary>Closes the store dialog of an interact when it is the open store.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="interact">The interact index the client closed.</param>
    public static void CloseStore(GameSession session, int interact)
    {
        var windows = StateOf(session).Windows;
        if (windows.TryGetValue(ServiceWindow.Store, out var open) && open.Interact == interact)
            windows.TryRemove(new KeyValuePair<ServiceWindow, OpenService>(ServiceWindow.Store, open));
    }

    /// <summary>Closes every map-bound service window of the session (<see cref="IsMapBound"/>).</summary>
    /// <param name="session">The player's session.</param>
    public static void CloseMapBound(GameSession session)
    {
        var windows = StateOf(session).Windows;
        foreach (var (window, open) in windows)
        {
            if (IsMapBound(window))
                windows.TryRemove(new KeyValuePair<ServiceWindow, OpenService>(window, open));
        }
    }

    /// <summary>Whether a kind of window closes when the player leaves its map: every kind but the Item Bag.</summary>
    /// <param name="window">The kind of window.</param>
    /// <returns>True when the window belongs to the map it was opened on.</returns>
    private static bool IsMapBound(ServiceWindow window) => window != ServiceWindow.ItemBag;

    /// <summary>The open windows of a session, created on first use.</summary>
    /// <param name="session">The player's session.</param>
    /// <returns>The session's state.</returns>
    private static OpenServicesSessionState StateOf(GameSession session) => session.Feature<OpenServicesSessionState>();
}
