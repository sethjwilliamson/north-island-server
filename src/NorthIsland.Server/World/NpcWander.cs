using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NorthIsland.GameData.Events;
using NorthIsland.Protocol.Messages.Events;

namespace NorthIsland.Server.World;

/// <summary>
/// A background service that moves the roaming NPCs of maps with players: random walks inside their box, chasing a nearby
/// player and starting a touch event.
/// </summary>
/// <param name="maps">The map data, for the NPCs of a scene.</param>
/// <param name="world">The online world, to find players and maps.</param>
/// <param name="events">The event engine, to start the touch event.</param>
/// <param name="options">The wander and chase settings.</param>
/// <param name="logger">The logger.</param>
public sealed class NpcWander(
    MapData maps,
    WorldState world,
    EventEngine events,
    IOptions<WorldOptions> options,
    ILogger<NpcWander> logger)
    : BackgroundService
{
    /// <summary>A roaming NPC with the walk it is on now.</summary>
    private sealed class Walker
    {
        /// <summary>The NPC's role number in its scene (1-based).</summary>
        public required ushort Role { get; init; }

        /// <summary>The NPC's record in the scene.</summary>
        public required EveNpc Npc { get; init; }

        /// <summary>The x position where the current walk started.</summary>
        public double FromX { get; set; }

        /// <summary>The y position where the current walk started.</summary>
        public double FromY { get; set; }

        /// <summary>The x position the current walk goes to.</summary>
        public double ToX { get; set; }

        /// <summary>The y position the current walk goes to.</summary>
        public double ToY { get; set; }

        /// <summary>When the current walk started.</summary>
        public DateTime Started { get; set; }

        /// <summary>When the NPC picks its next random target.</summary>
        public DateTime NextMove { get; set; }

        /// <summary>The player the NPC is chasing, null when it wanders.</summary>
        public GameSession? Chasing { get; set; }

        /// <summary>The walking speed in pixels per millisecond, from the NPC's speed 1 to 10 (2 when out of that range).</summary>
        public double PxPerMs => (Npc.Speed is >= 1 and <= 10 ? Npc.Speed : 2) / 8.0 * 0.16;

        /// <summary>The NPC's position at a time along the current walk.</summary>
        /// <param name="now">The time to evaluate at.</param>
        /// <returns>The x and y position.</returns>
        public (double X, double Y) At(DateTime now)
        {
            var dx = ToX - FromX;
            var dy = ToY - FromY;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1)
                return (ToX, ToY);
            var done = Math.Min(1, (now - Started).TotalMilliseconds * PxPerMs / length);
            return (FromX + dx * done, FromY + dy * done);
        }

        /// <summary>Limits a point to the NPC's wander box around its spawn point.</summary>
        /// <param name="x">The x position.</param>
        /// <param name="y">The y position.</param>
        /// <returns>The limited x and y position.</returns>
        public (double X, double Y) Clamp(double x, double y) =>
            (Math.Clamp(x, Npc.X - Npc.Unknown688, Npc.X + Npc.Unknown688), Math.Clamp(y, Npc.Y - Npc.Unknown692, Npc.Y + Npc.Unknown692));
    }

    /// <summary>The walkers of each map, created the first time the map has players.</summary>
    private readonly Dictionary<ushort, List<Walker>> _maps = [];

    /// <summary>Guards the walkers.</summary>
    private readonly Lock _lock = new();

    /// <summary>Whether an NPC roams: it has a movement kind of 4 or 5 and a wander box of positive size.</summary>
    /// <param name="npc">The NPC record.</param>
    /// <returns>True when it roams.</returns>
    public static bool Roams(EveNpc npc) => npc.MoveKind is 4 or 5 && npc.Unknown688 > 0 && npc.Unknown692 > 0;

    /// <summary>Tells a player who arrived on a map the positions and walk targets of the roaming NPCs and which monsters are hidden.</summary>
    /// <param name="session">The player's session.</param>
    public void SendPositions(GameSession session)
    {
        if (session.ActiveCharacter is not { } c)
            return;
        world.SendHiddenMonsters(session);
        var now = GameClock.UtcNow;
        lock (_lock)
        {
            if (!_maps.TryGetValue(c.MapId, out var walkers))
                return;
            foreach (var w in walkers)
            {
                var (x, y) = w.At(now);
                if ((int)x != w.Npc.X || (int)y != w.Npc.Y)
                    session.Send(EventMessages.NpcPlace(w.Role, (ushort)x, (ushort)y));
                if (w.ToX != x || w.ToY != y)
                    session.Send(EventMessages.NpcMove(w.Role, (ushort)w.ToX, (ushort)w.ToY, w.Npc.Speed));
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                Tick(GameClock.UtcNow);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Roaming monsters tick failed");
            }
        }
    }

    /// <summary>
    /// Moves every roaming NPC of the maps with players: chases or touches the nearest eligible player, else picks random
    /// targets, and sends the moves.
    /// </summary>
    /// <param name="now">The current time.</param>
    private void Tick(DateTime now)
    {
        if (options.Value.WanderMaxSeconds <= 0)
            return;
        List<(ushort Map, byte[] Packet)> moves = [];
        List<(GameSession Session, ushort Map, ushort Role, string Name)> touches = [];
        lock (_lock)
        {
            foreach (var map in world.OnlineMaps())
            {
                var players = world.OnMap(map).Where(s => s.ActiveCharacter is not null).ToList();
                foreach (var w in WalkersOf(map, now))
                {
                    var (x, y) = w.At(now);
                    var prey = options.Value.ChaseRadius > 0 && !world.MonsterIsOut(map, w.Role) ? Nearest(w, map, players, x, y, now) : null;
                    if (prey is { Session: var target, Distance: var distance })
                    {
                        if (distance <= options.Value.TouchRadius)
                        {
                            target.TouchCooldown[(map, w.Role)] = now.AddSeconds(6);
                            touches.Add((target, map, w.Role, w.Npc.Name));
                            Retarget(w, x, y, x, y, now);
                            w.Chasing = null;
                            w.NextMove = now + NextPause();
                            moves.Add((map, EventMessages.NpcMove(w.Role, (ushort)x, (ushort)y, w.Npc.Speed)));
                            continue;
                        }
                        var (tx, ty) = w.Clamp(target.ActiveCharacter!.X, target.ActiveCharacter.Y);
                        var moved = Math.Abs(tx - w.ToX) + Math.Abs(ty - w.ToY) > 10;
                        if (w.Chasing != target || moved && now - w.Started > TimeSpan.FromMilliseconds(700))
                        {
                            w.Chasing = target;
                            Retarget(w, x, y, tx, ty, now);
                            moves.Add((map, EventMessages.NpcMove(w.Role, (ushort)tx, (ushort)ty, w.Npc.Speed)));
                        }
                        continue;
                    }
                    w.Chasing = null;
                    if (w.NextMove > now)
                        continue;
                    var (nx, ny) = w.Clamp(w.Npc.X + GameRandom.Shared.Next(-w.Npc.Unknown688, w.Npc.Unknown688 + 1),
                        w.Npc.Y + GameRandom.Shared.Next(-w.Npc.Unknown692, w.Npc.Unknown692 + 1));
                    Retarget(w, x, y, nx, ny, now);
                    w.NextMove = now + NextPause();
                    moves.Add((map, EventMessages.NpcMove(w.Role, (ushort)nx, (ushort)ny, w.Npc.Speed)));
                }
            }
        }
        foreach (var (map, packet) in moves)
        {
            foreach (var s in world.OnMap(map))
                s.Send(packet);
        }
        foreach (var (session, map, role, name) in touches)
            Touch(session, map, role, name);
    }

    /// <summary>
    /// Starts the touch event of a roaming NPC under the player's gate: at once when the gate is free, else from a task that holds no gate and
    /// waits for it, so the tick never waits for a player (see <see cref="GameSession.TryExclusiveFrom"/> for the rule).
    /// </summary>
    /// <param name="session">The touched player's session.</param>
    /// <param name="map">The map the NPC roams.</param>
    /// <param name="role">The NPC's role number in the scene.</param>
    /// <param name="name">The NPC's name, for the log.</param>
    private void Touch(GameSession session, ushort map, ushort role, string name)
    {
        if (session.TryExclusiveNow(null, out var held))
        {
            using (held)
                TouchHeld(session, map, role, name);
            return;
        }
        _ = TouchLaterAsync(session, map, role, name);
    }

    /// <summary>Waits for the player's gate, holding no other, and starts the touch event.</summary>
    /// <param name="session">The touched player's session.</param>
    /// <param name="map">The map the NPC roams.</param>
    /// <param name="role">The NPC's role number in the scene.</param>
    /// <param name="name">The NPC's name, for the log.</param>
    /// <returns>The task.</returns>
    private async Task TouchLaterAsync(GameSession session, ushort map, ushort role, string name)
    {
        using (await session.ExclusiveAsync().ConfigureAwait(false))
            TouchHeld(session, map, role, name);
    }

    /// <summary>
    /// Starts the touch event under the player's gate when the player may still be touched: the connection is open, the player is on the map
    /// and free, past the arrival grace, and the monster is not out. A touch that fails is logged and leaves the other touches alone.
    /// </summary>
    /// <param name="session">The touched player's session.</param>
    /// <param name="map">The map the NPC roams.</param>
    /// <param name="role">The NPC's role number in the scene.</param>
    /// <param name="name">The NPC's name, for the log.</param>
    private void TouchHeld(GameSession session, ushort map, ushort role, string name)
    {
        try
        {
            if (session.Closing
                || session.ActiveCharacter is not { } c
                || c.MapId != map
                || !BattleHandlers.IsFree(session)
                || session.ArrivedUtc.AddSeconds(options.Value.ArrivalGraceSeconds) > GameClock.UtcNow
                || world.MonsterIsOut(map, role))
                return;
            logger.LogInformation("[{Session}] {Name} is touched by roaming NPC {Role} ({Npc})", session.Id, c.Name, role, name);
            events.Start(session, EventEngine.TriggerClick, role);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{Session}] touch by roaming NPC {Role} ({Npc}) on map {Map} failed", session.Id, role, name, map);
        }
    }

    /// <summary>
    /// Finds the nearest player the NPC may chase: free, not a team member, not hiding it or waiting for their own respawn of it,
    /// not in a touch cooldown or protected, and within the chase radius.
    /// </summary>
    /// <param name="w">The roaming NPC.</param>
    /// <param name="map">The map id.</param>
    /// <param name="players">The players on the map.</param>
    /// <param name="x">The NPC's current x position.</param>
    /// <param name="y">The NPC's current y position.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The player and the distance, or null when there is none.</returns>
    private (GameSession Session, double Distance)? Nearest(
        Walker w,
        ushort map,
        List<GameSession> players,
        double x,
        double y,
        DateTime now)
    {
        (GameSession, double)? best = null;
        foreach (var s in players)
        {
            var c = s.ActiveCharacter!;
            if (s.Account is { } account && world.Teams.LeaderOf(account.LoginId) is var leader and not 0 && leader != account.LoginId)
                continue;
            if (!BattleHandlers.IsFree(s)
                || s.ArrivedUtc.AddSeconds(options.Value.ArrivalGraceSeconds) > now
                || s.HiddenNpcs.TryGetValue((map, w.Role), out var hidden) && hidden > now
                || s.TouchCooldown.TryGetValue((map, w.Role), out var cooldown) && cooldown > now
                || s.Encounters.Immune(now)
                || s.Account is { } player && world.MonsterIsOutFor(map, w.Role, player.LoginId))
                continue;
            var distance = Math.Sqrt((c.X - x) * (c.X - x) + (c.Y - y) * (c.Y - y));
            if (distance <= options.Value.ChaseRadius && (best is null || distance < best.Value.Item2))
                best = (s, distance);
        }
        return best;
    }

    /// <summary>Starts a new walk for the NPC from one point to another.</summary>
    /// <param name="w">The roaming NPC.</param>
    /// <param name="fromX">The start x position.</param>
    /// <param name="fromY">The start y position.</param>
    /// <param name="toX">The target x position.</param>
    /// <param name="toY">The target y position.</param>
    /// <param name="now">The current time.</param>
    private static void Retarget(
        Walker w,
        double fromX,
        double fromY,
        double toX,
        double toY,
        DateTime now)
    {
        (w.FromX, w.FromY, w.ToX, w.ToY, w.Started) = (fromX, fromY, toX, toY, now);
    }

    /// <summary>The roaming NPCs of a map, created from the map's scene the first time they are asked for.</summary>
    /// <param name="map">The map id.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The walkers.</returns>
    private List<Walker> WalkersOf(ushort map, DateTime now)
    {
        if (_maps.TryGetValue(map, out var walkers))
            return walkers;
        walkers = [];
        if (maps.Scene(map) is { } scene)
        {
            for (var i = 0; i < scene.Npcs.Count; i++)
            {
                var npc = scene.Npcs[i];
                if (Roams(npc))
                {
                    walkers.Add(new Walker
                    {
                        Role = (ushort)(i + 1), Npc = npc, FromX = npc.X, FromY = npc.Y, ToX = npc.X, ToY = npc.Y, Started = now,
                        NextMove = now + NextPause(),
                    });
                }
            }
        }
        _maps[map] = walkers;
        return walkers;
    }

    /// <summary>A random pause between wander walks, between the configured minimum and maximum seconds.</summary>
    /// <returns>The pause.</returns>
    private TimeSpan NextPause()
    {
        var min = Math.Max(1, options.Value.WanderMinSeconds);
        var max = Math.Max(min, options.Value.WanderMaxSeconds);
        return TimeSpan.FromSeconds(min + GameRandom.Shared.NextDouble() * (max - min));
    }
}
