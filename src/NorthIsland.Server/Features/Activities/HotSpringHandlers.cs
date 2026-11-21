using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NorthIsland.Server.Features.Activities;

/// <summary>
/// Gives a player bonus HP and SP points for the time spent standing in a hot spring map's water: at most
/// <see cref="HotSpringOptions.MaxPerVisit"/> steps per visit, a visit lasting from entering the map to leaving it however often the client
/// says it steps into the water, and at most <see cref="HotSpringOptions.MaxTotal"/> points in all.
/// </summary>
/// <param name="world">The world state, whose leave and map leave events stop the bathing and end the visit.</param>
/// <param name="combat">The combat stats, resent after a point is added.</param>
/// <param name="options">The activity options giving the hot spring settings.</param>
/// <param name="logger">Logger for the bathing messages.</param>
public sealed class HotSpringHandlers(
    WorldState world,
    CombatStats combat,
    IOptions<ActivityOptions> options,
    ILogger<HotSpringHandlers> logger)
{
    /// <summary>The hot spring visit of one session: the map it is on and the steps it gave.</summary>
    private sealed class Visit
    {
        /// <summary>The map id of the visit; 0 when the player is not on a visit.</summary>
        public ushort Map { get; set; }

        /// <summary>The bonus steps the visit gave so far.</summary>
        public int Steps { get; set; }

        /// <summary>Whether the visit already logged that it reached its cap, so the client's repeated step-ins log it only once.</summary>
        public bool CapLogged { get; set; }
    }

    /// <summary>Subscribes to the world's events: a player leaving the world or their map stops bathing and ends their visit.</summary>
    internal void Start()
    {
        world.Leaving += Stop;
        world.MapLeaving += EndVisit;
    }

    /// <summary>Stops the player's bathing and ends their visit, so the next map entry starts a new one.</summary>
    /// <param name="session">The player's session.</param>
    private static void EndVisit(GameSession session)
    {
        Stop(session);
        session.Feature<Visit>().Map = 0;
    }

    /// <summary>Stops the sender's bathing when the client reports leaving the water.</summary>
    /// <param name="s">The player's session.</param>
    /// <param name="body">The packet body, not read.</param>
    internal static ValueTask OnLeave(GameSession s, ReadOnlyMemory<byte> body)
    {
        Stop(s);
        return ValueTask.CompletedTask;
    }

    /// <summary>Cancels the player's bathing loop, if any.</summary>
    /// <param name="session">The player's session.</param>
    public static void Stop(GameSession session)
    {
        if (session.Activities.Spring is not { } spring)
            return;
        session.Activities.Spring = null;
        spring.Cancel();
        spring.Dispose();
    }

    /// <summary>
    /// Starts the bathing loop when the player steps into the water of a hot spring map. The client sends this again while the player walks
    /// in the water; that restarts the step timer but not the visit's count of steps.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body, not read.</param>
    internal ValueTask OnEnter(GameSession session, ReadOnlyMemory<byte> body)
    {
        var o = options.Value.HotSpring;
        if (!o.Enabled
            || session.State != SessionState.InWorld
            || session.ActiveCharacter is not { } character
            || o.MinutesPerPoint <= 0
            || o.MaxPerVisit <= 0
            || !o.Maps.Contains(character.MapId))
            return ValueTask.CompletedTask;
        var visit = session.Feature<Visit>();
        if (visit.Map != character.MapId)
            (visit.Map, visit.Steps, visit.CapLogged) = (character.MapId, 0, false);
        if (visit.Steps >= o.MaxPerVisit || character.SpringBonus >= o.MaxTotal)
        {
            // the normal client sends a step-in again on every move in the water, so the cap is logged once per visit
            if (!visit.CapLogged)
                logger.LogInformation(
                    "[{Session}] {Name}: steps into the hot spring on map {Map}, but has {Steps}/{Max} steps this visit and {Bonus}/{Total} points",
                    session.Id, character.Name, character.MapId, visit.Steps, o.MaxPerVisit, character.SpringBonus, o.MaxTotal);
            visit.CapLogged = true;
            return ValueTask.CompletedTask;
        }
        Stop(session);
        var cts = new CancellationTokenSource();
        session.Activities.Spring = cts;
        logger.LogInformation(
            "[{Session}] {Name} steps into the hot spring on map {Map} ({Steps}/{Max} steps this visit, {Bonus} points so far)",
            session.Id,
            character.Name,
            character.MapId,
            visit.Steps,
            o.MaxPerVisit,
            character.SpringBonus);
        _ = BatheAsync(session, visit, cts.Token);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The bathing loop: after each interval adds the bonus points, until the visit has its most steps, the character has the most points, the
    /// visit ends or the loop is cancelled.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="visit">The visit the steps count toward.</param>
    /// <param name="token">Cancels the loop.</param>
    private async Task BatheAsync(GameSession session, Visit visit, CancellationToken token)
    {
        var o = options.Value.HotSpring;
        var map = visit.Map;
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromMinutes(o.MinutesPerPoint), token).ConfigureAwait(false);
                using var exclusive = await session.ExclusiveAsync().ConfigureAwait(false); // the step changes the stats between packets
                if (token.IsCancellationRequested || session.State != SessionState.InWorld || session.ActiveCharacter is not { } character
                    || character.MapId != map || visit.Map != map || visit.Steps >= o.MaxPerVisit || character.SpringBonus >= o.MaxTotal)
                    return;
                visit.Steps++;
                character.SpringBonus += Math.Min(o.PointsPerStep, o.MaxTotal - character.SpringBonus);
                combat.Send(session);
                logger.LogInformation(
                    "[{Session}] {Name}: hot spring point {Step}/{Max}, bonus now {Bonus}/{Total}",
                    session.Id,
                    character.Name,
                    visit.Steps,
                    o.MaxPerVisit,
                    character.SpringBonus,
                    o.MaxTotal);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
