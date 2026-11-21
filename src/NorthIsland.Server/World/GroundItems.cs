using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NorthIsland.Protocol.Messages.Items;

namespace NorthIsland.Server.World;

/// <summary>
/// The things lying on the ground of each map: map things that come back after being taken, and things players drop, which
/// vanish after a while.
/// </summary>
/// <param name="maps">The map data, for the things placed on a map.</param>
/// <param name="world">The online world, to reach the players on a map.</param>
/// <param name="options">The respawn and expiry times.</param>
/// <param name="logger">The logger.</param>
public sealed class GroundItems(
    MapData maps,
    WorldState world,
    IOptions<WorldOptions> options,
    ILogger<GroundItems> logger)
{
    /// <summary>Random source for where a dropped thing lands: returns a number from 0 up to but not including its argument. Replaceable by tests.</summary>
    public Func<int, int> Roll { get; set; } = GameRandom.Shared.Next;

    /// <summary>The highest index of a thing on a map's ground; indexes start at 1.</summary>
    private const int MaxIndex = 255;

    /// <summary>One thing on a map's ground.</summary>
    /// <param name="Thing">What lies there.</param>
    /// <param name="X">The x position.</param>
    /// <param name="Y">The y position.</param>
    /// <param name="Respawns">True for a thing from the map data, which comes back after it is taken.</param>
    /// <param name="Expires">When a dropped thing vanishes, null for a map thing.</param>
    /// <param name="Extra">Pixels the client adds to Y when sorting it among characters and map objects (eve.Emg goods @72); 0 for a dropped thing.</param>
    private sealed record Slot(
        GroundThing Thing,
        ushort X,
        ushort Y,
        bool Respawns,
        DateTime? Expires,
        int Extra = 0);

    /// <summary>The ground of one map: its things by index and the taken map things waiting to come back.</summary>
    private sealed class MapGround
    {
        /// <summary>The things by index; index 0 is unused and null marks a free index.</summary>
        public readonly Slot?[] Slots = new Slot?[MaxIndex + 1];

        /// <summary>The taken map things with the time each comes back.</summary>
        public readonly List<(DateTime Due, Slot Slot)> Respawns = [];
    }

    /// <summary>The ground of each map, built the first time the map is used.</summary>
    private readonly ConcurrentDictionary<ushort, MapGround> _maps = new();

    /// <summary>Sends a player who arrived the list of things on the ground of their map, when there are any.</summary>
    /// <param name="session">The player's session.</param>
    public void SendList(GameSession session)
    {
        var ground = Ground(session.ActiveCharacter!.MapId);
        List<GroundEntry> entries;
        lock (ground)
            entries = [.. Enumerable.Range(1, MaxIndex).Where(i => ground.Slots[i] is not null).Select(i =>
            {
                var s = ground.Slots[i]!;
                return new GroundEntry((ushort)i, s.Thing.ItemId, s.X, s.Y, s.Thing.Gold, s.Extra);
            })];
        if (entries.Count > 0)
            session.Send(GroundMessages.List(entries));
    }

    /// <summary>Finds the indexes of the plain items (not gold) with an item id on a map's ground.</summary>
    /// <param name="mapId">The map id.</param>
    /// <param name="itemId">The item id to look for.</param>
    /// <returns>The indexes.</returns>
    public List<ushort> Find(ushort mapId, ushort itemId)
    {
        var ground = Ground(mapId);
        lock (ground)
            return [
                .. Enumerable.Range(1, MaxIndex).Where(i => ground.Slots[i] is { } s && s.Thing.ItemId == itemId && s.Thing.Gold == 0).Select(i => (ushort)i)
            ];
    }

    /// <summary>
    /// Takes the thing at an index off the player's map when a check accepts it: frees the index, queues a map thing to come back, and tells the
    /// map to remove it. The check runs under the map's lock on the very thing that is then taken, so a thing dropped into the freed index in
    /// between cannot be taken in its place. The check must be quick and must not take other locks.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="index">The ground index.</param>
    /// <param name="refusal">Gets the thing and its x and y position and returns why it may not be taken, or null to take it.</param>
    /// <param name="refused">The reason the check gave, or null when the thing was taken or the index is empty.</param>
    /// <returns>The thing taken, or null when the index is empty or invalid or the check refused it.</returns>
    public GroundThing? TakeIf(GameSession session, ushort index, Func<GroundThing, ushort, ushort, string?> refusal, out string? refused)
    {
        var mapId = session.ActiveCharacter!.MapId;
        var ground = Ground(mapId);
        Slot? slot;
        refused = null;
        lock (ground)
        {
            slot = index is >= 1 and <= MaxIndex ? ground.Slots[index] : null;
            if (slot is null)
                return null;
            refused = refusal(slot.Thing, slot.X, slot.Y);
            if (refused is not null)
                return null;
            ground.Slots[index] = null;
            if (slot.Respawns)
                ground.Respawns.Add((GameClock.UtcNow.AddSeconds(Math.Max(1, options.Value.GroundItemRespawnSeconds)), slot));
        }
        foreach (var other in world.OnMap(mapId))
            other.Send(GroundMessages.Remove(index, animate: ReferenceEquals(other, session)));
        return slot.Thing;
    }

    /// <summary>Puts a thing on the ground around the player's position and tells the map; it vanishes after a while.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="thing">The item or gold dropped.</param>
    /// <returns>True when it was placed; false when no index is free.</returns>
    public bool Drop(GameSession session, GroundThing thing)
    {
        var character = session.ActiveCharacter!;
        var ground = Ground(character.MapId);
        var slot = new Slot(thing, Scatter(character.X), Scatter(character.Y), Respawns: false,
            GameClock.UtcNow.AddSeconds(Math.Max(1, options.Value.DroppedItemSeconds)));
        lock (ground)
        {
            var free = Array.FindIndex(ground.Slots, 1, s => s is null);
            if (free < 1)
                return false;
            ground.Slots[free] = slot;
        }
        foreach (var other in world.OnMap(character.MapId))
        {
            var own = ReferenceEquals(other, session);
            other.Send(thing.Gold > 0
                ? GroundMessages.AddGold(thing.Gold, slot.X, slot.Y, dropped: own)
                : GroundMessages.Add(thing.ItemId, slot.X, slot.Y, dropped: own));
        }
        return true;
    }

    /// <summary>Picks where a dropped thing lands on one axis.</summary>
    /// <param name="position">The player's position on the axis.</param>
    /// <returns>A position at most <c>DropScatterPixels</c> away, kept on the map's coordinates.</returns>
    private ushort Scatter(ushort position)
    {
        var spread = options.Value.DropScatterPixels;
        return spread <= 0 ? position : (ushort)Math.Clamp(position + Roll(2 * spread + 1) - spread, 0, ushort.MaxValue);
    }

    /// <summary>Runs once a second: removes dropped things that expired and brings back map things that are due, telling each map.</summary>
    public void Tick()
    {
        var now = GameClock.UtcNow;
        foreach (var (mapId, ground) in _maps)
        {
            List<Slot> back = [];
            List<ushort> expired = [];
            lock (ground)
            {
                for (var i = 1; i <= MaxIndex; i++)
                {
                    if (ground.Slots[i] is { Expires: { } expires } && expires <= now)
                    {
                        ground.Slots[i] = null;
                        expired.Add((ushort)i);
                    }
                }
                foreach (var due in ground.Respawns.Where(r => r.Due <= now).ToList())
                {
                    var free = Array.FindIndex(ground.Slots, 1, s => s is null);
                    if (free < 1)
                        break;
                    ground.Respawns.Remove(due);
                    ground.Slots[free] = due.Slot;
                    back.Add(due.Slot);
                }
            }
            foreach (var index in expired)
                Broadcast(mapId, GroundMessages.Remove(index, animate: false));
            foreach (var slot in back)
            {
                Broadcast(mapId, GroundMessages.Add(slot.Thing.ItemId, slot.X, slot.Y, extra: slot.Extra));
                logger.LogInformation("Map {Map}: item {Item} back at ({X}, {Y})", mapId, slot.Thing.ItemId, slot.X, slot.Y);
            }
            if (expired.Count > 0)
                logger.LogInformation("Map {Map}: {Count} dropped thing(s) vanished", mapId, expired.Count);
        }
    }

    /// <summary>Sends a packet to every player on a map.</summary>
    /// <param name="mapId">The map id.</param>
    /// <param name="packet">The packet to send.</param>
    private void Broadcast(ushort mapId, byte[] packet)
    {
        foreach (var session in world.OnMap(mapId))
            session.Send(packet);
    }

    /// <summary>The ground of a map, created from the map's own things on first use.</summary>
    /// <param name="mapId">The map id.</param>
    /// <returns>The map's ground.</returns>
    private MapGround Ground(ushort mapId) => _maps.GetOrAdd(mapId, id =>
    {
        var ground = new MapGround();
        var goods = maps.Scene(id)?.Goods ?? [];
        var index = 1;
        foreach (var g in goods.Where(g => g.Unknown64 is > 0 and <= ushort.MaxValue).Take(MaxIndex))
            ground.Slots[index++] = new Slot(new GroundThing((ushort)g.Unknown64, 1, 0), Clamp(g.X), Clamp(g.Y), Respawns: true, Expires: null, Extra: g.Unknown72);
        return ground;
    });

    /// <summary>Limits a coordinate to the range of a ushort.</summary>
    /// <param name="v">The coordinate.</param>
    /// <returns>The limited coordinate.</returns>
    private static ushort Clamp(int v) => (ushort)Math.Clamp(v, 0, ushort.MaxValue);
}
