using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NorthIsland.Protocol.IO;
using NorthIsland.Protocol.Messages.Items;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.Features.Activities;

/// <summary>Starts and stops the fishing loop of a player and puts the catches in the bag.</summary>
/// <param name="world">The world state, used to broadcast the start and stop.</param>
/// <param name="items">The item records.</param>
/// <param name="lifeSkills">The life skills, trained by each catch.</param>
/// <param name="options">The activity options giving the attempt interval and catch chance.</param>
/// <param name="logger">Logger for the fishing messages.</param>
public sealed class FishingHandlers(
    WorldState world,
    ItemData items,
    LifeSkills lifeSkills,
    IOptions<ActivityOptions> options,
    ILogger<FishingHandlers> logger)
{
    /// <summary>The items a catch is picked from: the item id and its weight for each of the three tool kinds.</summary>
    public static readonly ImmutableArray<(ushort ItemId, ImmutableArray<int> Weights)> Catches =
        [
            (41093, [60, 45, 35]), // Fish
            (41101, [10, 20, 25]), // Cuttlefish
            (46074, [20, 20, 15]), // Fresh Fish Scale
            (46148, [10, 10, 15]), // Jelly Fish
            (46075, [0, 5, 10]), // Sea Fish Scale
            (44004, [6, 5, 4]), // White Clay
            (45005, [5, 4, 3]), // Wood
            (42014, [3, 4, 4]), // Iron Ore
            (42020, [0, 2, 3]), // Pure Iron Ore
            (46014, [3, 2, 1]), // Wild Rabbit Fur
            (46010, [0, 3, 4]), // Brown Fox Fur
            (42045, [0, 2, 4]), // Emerald
            (42047, [0, 1, 3]), // Sapphire
            (42052, [0, 0, 1]), // Diamond
        ];

    /// <summary>The item sub type of fishing tools.</summary>
    public const byte FishingRodSubType = 107;

    /// <summary>Stops the sender's fishing when the client asks for it.</summary>
    /// <param name="s">The player's session.</param>
    /// <param name="body">The packet body, not read.</param>
    internal ValueTask OnStop(GameSession s, ReadOnlyMemory<byte> body)
    {
        Stop(s, world);
        return ValueTask.CompletedTask;
    }

    /// <summary>Cancels the player's fishing loop, if any, and tells the player and the others on the map that it stopped.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="world">The world state, used to broadcast to the map.</param>
    public static void Stop(GameSession session, WorldState world)
    {
        if (session.Activities.Fishing is not { } fishing || session.Account is null)
            return;
        session.Activities.Fishing = null;
        fishing.Cancel();
        fishing.Dispose();
        var packet = new PacketWriter(Action23.Code, Action23.FishingStopped).I32(session.Account.LoginId).ToArray();
        session.Send(packet);
        world.Broadcast(session, packet, sameMapOnly: true);
    }

    /// <summary>Starts fishing with the tool in a bag slot and tells the player and the others on the map.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the bag slot of the fishing tool.</param>
    internal ValueTask OnStart(GameSession session, ReadOnlyMemory<byte> body)
    {
        var character = session.ActiveCharacter;
        if (session.State != SessionState.InWorld || character is null || session.Battle is not null || body.Length < 1)
            return ValueTask.CompletedTask;
        var slot = body.Span[0];
        if (character.Bag.Find(b => b.Slot == slot) is not { } rod || items[rod.ItemId] is not { SubType: FishingRodSubType } record)
        {
            logger.LogWarning("[{Session}] {Name}: fishing without a fishing tool in bag slot {Slot}", session.Id, character.Name, slot);
            return ValueTask.CompletedTask;
        }
        Stop(session, world);
        var kind = Math.Max((byte)1, record.ShopColumn);
        var cts = new CancellationTokenSource();
        session.Activities.Fishing = cts;
        var packet = new PacketWriter(Action23.Code, Action23.FishingStarted).I32(session.Account!.LoginId).U8(kind).ToArray();
        session.Send(packet);
        world.Broadcast(session, packet, sameMapOnly: true);
        logger.LogInformation("[{Session}] {Name} starts fishing with {Rod} (kind {Kind})", session.Id, character.Name, record.Name, kind);
        _ = FishAsync(session, rod.ItemId, kind, character.MapId, cts.Token);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The fishing loop: waits for each attempt and puts what it catches in the bag until cancelled. Before each attempt it checks that the
    /// player still holds the fishing tool, is not in a battle and is still on the map they started on; otherwise it stops the fishing.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="rodId">The item id of the fishing tool the player started with.</param>
    /// <param name="kind">The tool kind, 1 and up, choosing the weight column.</param>
    /// <param name="map">The map id the player started fishing on.</param>
    /// <param name="token">Cancels the loop.</param>
    private async Task FishAsync(
        GameSession session,
        ushort rodId,
        byte kind,
        ushort map,
        CancellationToken token)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.Value.FishingSeconds)), token).ConfigureAwait(false);
                using var exclusive = await session.ExclusiveAsync().ConfigureAwait(false); // the catch goes in the bag between packets
                if (token.IsCancellationRequested || session.ActiveCharacter is not { } character)
                    return;
                var why = session.State != SessionState.InWorld ? "left the world"
                    : session.Battle is not null ? "is in a battle"
                    : character.MapId != map ? "left the map"
                    : !character.Bag.Exists(b => b.ItemId == rodId) ? "no longer holds the fishing tool"
                    : null;
                if (why is not null)
                {
                    logger.LogWarning("[{Session}] {Name}: fishing stops, the player {Why}", session.Id, character.Name, why);
                    Stop(session, world);
                    return;
                }
                if (GameRandom.Shared.Next(100) >= options.Value.FishingCatchPercent)
                    continue;
                var maxRank = MaxCatchRank(kind, LifeSkills.FishingLevel(character));
                var catchId = Pick(kind, GameRandom.Shared, id => items[id] is { } item && item.ShopColumn <= maxRank);
                lifeSkills.TrainFishing(session); // a catch trains the skill even when the bag is full
                if (items[catchId] is null || Bag.Add(character, items, catchId, 1).Count == 0)
                {
                    logger.LogInformation("[{Session}] {Name}: a fish bit but the bag is full", session.Id, character.Name);
                    continue;
                }
                session.Send(GroundMessages.ItemObtained(catchId, 1));
                logger.LogInformation("[{Session}] {Name} catches {Item}", session.Id, character.Name, items[catchId]!.Name);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Works out the highest item rank a fishing tool can catch at a fishing skill level.</summary>
    /// <param name="kind">The tool kind: 1 Simple, 2 Great, 3 and up Luxury.</param>
    /// <param name="level">The fishing skill level.</param>
    /// <returns>The highest rank: the Simple tool reaches 6 at level 6, the Great tool 8 at level 6, and the Luxury tool 10 at level 4.</returns>
    public static int MaxCatchRank(byte kind, int level) => kind switch
    {
        <= 1 => Math.Min(6, level),
        2 => Math.Min(8, level + 2),
        _ => level switch { <= 1 => 5, 2 => 6, 3 => 7, _ => 10 },
    };

    /// <summary>Picks one item of <see cref="Catches"/> at random by the weights of a tool kind.</summary>
    /// <param name="kind">The tool kind, 1 to 3; others are clamped.</param>
    /// <param name="rng">The random source.</param>
    /// <param name="allowed">Tells which items can be caught; null allows them all.</param>
    /// <returns>The item id.</returns>
    public static ushort Pick(byte kind, Random rng, Func<ushort, bool>? allowed = null)
    {
        var column = Math.Clamp(kind - 1, 0, 2);
        var open = Catches.Where(c => allowed is null || allowed(c.ItemId)).ToList();
        var roll = rng.Next(open.Sum(c => c.Weights[column]));
        foreach (var (itemId, weights) in open)
        {
            roll -= weights[column];
            if (roll < 0)
                return itemId;
        }
        return Catches[0].ItemId;
    }
}
