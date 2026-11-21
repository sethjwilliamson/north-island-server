using Microsoft.Extensions.Logging;
using NorthIsland.Protocol;
using NorthIsland.Protocol.Messages.World;

namespace NorthIsland.Server.Features.Activities;

/// <summary>Builds the activity list for the 2014 client and handles its list and teleport requests.</summary>
/// <param name="calendar">The event calendar giving the clock, the weekly activities and the running events.</param>
/// <param name="world">The world state, used to move a teleporting player.</param>
/// <param name="travel">The teleport rule.</param>
/// <param name="logger">Logger for the teleport messages.</param>
public sealed class ActivityHandlers(
    EventCalendar calendar,
    WorldState world,
    TravelRules travel,
    ILogger<ActivityHandlers> logger)
{
    /// <summary>The activity entries at a given time, weekly activities first, then running events.</summary>
    /// <param name="now">The time to build the list for.</param>
    /// <returns>The entries, at most <see cref="ActivitySchedule.MaxEntries"/>.</returns>
    public List<ActivityEntry> Entries(DateTime now) =>
        ActivitySchedule.Build(calendar.File, calendar.Active(now), calendar.ActivitiesAlwaysOpen, now);

    /// <summary>The packet carrying the whole activity list.</summary>
    /// <param name="now">The time to build the list for.</param>
    /// <returns>The list packet.</returns>
    public byte[] ListPacket(DateTime now)
    {
        var entries = Entries(now);
        return ActivityMessages.List(ActivitySchedule.Weekday(now.DayOfWeek), ActivitySchedule.Version(entries), entries);
    }

    /// <summary>Answers the list request by sending the whole list to a 2014 client in the world.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body, not read.</param>
    internal ValueTask OnList(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || session.Client != ClientGeneration.Client2014)
            return ValueTask.CompletedTask;
        session.Send(ListPacket(calendar.Clock()));
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Teleports the player to a running activity chosen by its 1-based place in the list. Refused, as the client refuses it
    /// (activityTeleportRefused), by the teleport rule (busy states, an event among them, and a map that cannot be left or reached), on a
    /// vehicle and in a team.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the 1-based entry id.</param>
    internal ValueTask OnTeleport(GameSession session, ReadOnlyMemory<byte> body)
    {
        var character = session.ActiveCharacter;
        if (session.State != SessionState.InWorld || character is null || session.Account is null || body.Length < 1)
            return ValueTask.CompletedTask;
        var now = calendar.Clock();
        var entries = Entries(now);
        var id = body.Span[0];
        var today = ActivitySchedule.Weekday(now.DayOfWeek);
        if (id < 1 || id > entries.Count || entries[id - 1] is not { State: 2 } entry || entry.Weekday != 0 && entry.Weekday != today)
        {
            session.Send(ActivityMessages.Notice(1));
            logger.LogInformation("[{Session}] activity teleport {Id}: not running", session.Id, id);
            return ValueTask.CompletedTask;
        }
        if (entry.Map == 0 || entry.X == 0 || entry.Y == 0 || character.Level < entry.MinLevel)
        {
            logger.LogInformation("[{Session}] activity teleport {Id} ({Name}): no teleport or level too low", session.Id, id, entry.Name);
            return ValueTask.CompletedTask;
        }
        var refusal = travel.TeleportRefusal(session, character, entry.Map)
            ?? (session.Vehicle is not null ? "on a vehicle" : world.Teams.LeaderOf(session.Account.LoginId) != 0 ? "in a team" : null);
        if (refusal is not null)
        {
            logger.LogInformation("[{Session}] {Name}: activity teleport {Id} ({Activity}) refused: {Why}", session.Id, character.Name, id, entry.Name,
                refusal);
            return ValueTask.CompletedTask;
        }
        logger.LogInformation("[{Session}] {Name} teleports to the activity {Activity} (map {Map})", session.Id, character.Name, entry.Name, entry.Map);
        world.ChangeMap(session, new DoorWarp(entry.Map, entry.X, entry.Y, 0));
        return ValueTask.CompletedTask;
    }
}
