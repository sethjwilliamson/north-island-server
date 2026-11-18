using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NorthIsland.GameData.Items;
using NorthIsland.Protocol.Messages.Items;

namespace NorthIsland.Server.Features.Battles;

/// <summary>Handles the training vouchers, tickets and fares, their timed state and the teleport to the Training Island.</summary>
/// <param name="world">The world state used to move the player.</param>
/// <param name="options">The item options holding the ticket duration and the longest allowed training time.</param>
/// <param name="logger">The logger.</param>
public sealed class TrainingIslandHandlers(
    WorldState world,
    IOptions<ItemOptions> options,
    ILogger<TrainingIslandHandlers> logger)
{
    /// <summary>The timed-effect kind under which the training state is stored on a character.</summary>
    public const string Kind = "training";

    /// <summary>Item ids of the Training Voucher, Ticket and Fare.</summary>
    public const ushort Voucher = 34181, Ticket = 34253, Fare = 34258; // 34181 Training Voucher, 34253 Training Ticket, 34258 Training Fare

    /// <summary>Map id of the Training Island.</summary>
    public const ushort IslandMap = 13050; // Training Island

    /// <summary>Map ids of the Training Island and the areas its doors lead to.</summary>
    private const ushort FirstIslandMap = 13050, LastIslandMap = 13057; // 13050 Training Island, 13051 Beach Area, 13052 1st Cave Area, 13053 2nd Cave Area, 13054 1st Ocean Area, 13055 2nd Ocean Area, 13056 1st Forest Area, 13057 2nd Forest Area

    /// <summary>Whether a map is the Training Island or one of its areas.</summary>
    /// <param name="mapId">The map id.</param>
    /// <returns>True for the island and every area reached through its doors.</returns>
    public static bool IsIslandMap(int mapId) => mapId is >= FirstIslandMap and <= LastIslandMap;

    /// <summary>Coordinates on the Training Island where players arrive.</summary>
    public const int IslandX = 2632, IslandY = 1344;

    /// <summary>The training time an item grants.</summary>
    /// <param name="itemId">The item id.</param>
    /// <returns>The minutes granted, 0 when the item is not a training item.</returns>
    public int MinutesOf(ushort itemId) => itemId switch
    {
        Voucher => 6 * 60, // training voucher: 6 hours
        Fare => 60, // training fare: 60 minutes
        Ticket => options.Value.ItemUse.TrainingTicketMinutes, // training ticket: minutes from the options
        _ => 0, // not a training item: none
    };

    /// <summary>Uses a training item: starts the timed state and removes one item from the stack.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="c">The player's character.</param>
    /// <param name="stack">The bag stack that holds the item.</param>
    /// <param name="item">The item's record.</param>
    /// <returns>True when the item is a training item, even if the use was refused.</returns>
    public bool TryUse(GameSession session, Character c, BagItem stack, ItemRecord item)
    {
        var minutes = MinutesOf(item.Id);
        if (minutes <= 0)
            return false;
        var now = TimedEffects.Now;
        var running = TimedEffects.Find(c, Kind, now);
        if (running is not null)
        {
            session.Send(TrainingMessages.AlreadyRunning());
            return true;
        }
        var max = options.Value.ItemUse.TrainingMaxMinutes;
        if (max > 0 && minutes > max)
        {
            session.Send(TrainingMessages.LimitReached());
            return true;
        }
        TimedEffects.Add(c, Kind, 1, 0, item.Id, TimeSpan.FromMinutes(minutes), now);
        stack.Count--;
        session.Send(ItemMessages.BagSlotDecrease(stack.Slot, 1));
        if (stack.Count == 0)
            c.Bag.Remove(stack);
        session.Send(ItemMessages.UsedSound());
        session.Send(TrainingMessages.State((ushort)minutes, sound: true));
        logger.LogInformation("[{Session}] {Name} uses {Item}: training state for {Minutes} minutes", session.Id, c.Name, item.Name, minutes);
        return true;
    }

    /// <summary>Sends the remaining training time when the player enters the world, if the state is running.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="c">The player's character.</param>
    public static void SendOnLogin(GameSession session, Character c)
    {
        var now = TimedEffects.Now;
        if (TimedEffects.Find(c, Kind, now) is { } e)
            session.Send(TrainingMessages.State((ushort)Math.Min(ushort.MaxValue, TimedEffects.Minutes(e, now))));
    }

    /// <summary>Handles the client's training request: <c>1</c> sends the state again, <c>2</c> teleports to the island.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal ValueTask OnRequest(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || session.ActiveCharacter is not { } c || body.Length < 1)
            return ValueTask.CompletedTask;
        var now = TimedEffects.Now;
        if (TimedEffects.Find(c, Kind, now) is not { } e)
            return ValueTask.CompletedTask;
        switch (body.Span[0])
        {
            case 1: // 23/111/1: sends the remaining training time again
                session.Send(TrainingMessages.State((ushort)Math.Min(ushort.MaxValue, TimedEffects.Minutes(e, now))));
                break;
            case 2: // 23/111/2: teleports to the training island
                Teleport(session, c);
                break;
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>Moves the player to the Training Island unless something prevents it, in which case it only logs why.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="c">The player's character.</param>
    private void Teleport(GameSession session, Character c)
    {
        string? why = session.Battle is not null ? "in a battle"
            : session.Stalls.Any ? "at a stall"
                : session.Vehicle is not null ? "on a vehicle"
                    : session.Event is not null || session.Minigame is not null ? "in an event or a game"
                        : TentHandlers.IsHouseMap(c.MapId) ? "in a house"
                            : MagicMap.NoTeleportMaps.Contains(c.MapId) ? "on a map that cannot be left this way"
                                : c.MapId == IslandMap ? "already there"
                                    : null;
        if (why is not null)
        {
            logger.LogInformation("[{Session}] {Name}: Training Island teleport refused: {Why}", session.Id, c.Name, why);
            return;
        }
        logger.LogInformation("[{Session}] {Name} teleports to the Training Island", session.Id, c.Name);
        world.ChangeMap(session, new DoorWarp(IslandMap, IslandX, IslandY, 0));
    }
}
