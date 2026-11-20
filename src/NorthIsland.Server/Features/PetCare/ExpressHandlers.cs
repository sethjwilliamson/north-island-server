using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NorthIsland.Protocol.Messages.Items;

namespace NorthIsland.Server.Features.PetCare;

/// <summary>Runs the pet express: a pet carries bag stacks to another online player and delivers them when its trip ends.</summary>
/// <param name="items">The item data, for bag stacking.</param>
/// <param name="world">The world state, to find the target player's session.</param>
/// <param name="options">The item options, which hold the trip time.</param>
/// <param name="logger">The logger.</param>
public sealed class ExpressHandlers(
    ItemData items,
    WorldState world,
    IOptions<ItemOptions> options,
    ILogger<ExpressHandlers> logger)
{
    /// <summary>The most bag stacks one trip carries.</summary>
    public const int MaxStacks = 5;

    /// <summary>Starts a trip after checking the pet, the stacks and the target; a refused check is answered with an express error.</summary>
    /// <param name="session">The sender's session.</param>
    /// <param name="body">The packet body: the pet's NPC id, the target's login id, then pairs of bag slot and count.</param>
    internal ValueTask OnStart(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld
            || session.ActiveCharacter is not { } c
            || session.Account is null
            || session.PetJobs.Express is not null
            || body.Length < 8)
            return ValueTask.CompletedTask;
        var petId = BinaryPrimitives.ReadInt32LittleEndian(body.Span);
        var targetId = BinaryPrimitives.ReadInt32LittleEndian(body.Span[4..]);
        List<(byte Slot, byte Count)> stacks = [];
        for (var i = 8; i + 1 < body.Length && stacks.Count < MaxStacks; i += 2)
            stacks.Add((body.Span[i], body.Span[i + 1]));
        var pet = c.Pets.Find(p => p.NpcId == petId);
        if (pet is null || stacks.Count == 0 || c.ActivePetId == petId || session.Battle is not null || session.Trades.Items is not null)
        {
            logger.LogInformation("[{Session}] {Name}: express refused (pet {Pet}, {Count} stacks)", session.Id, c.Name, petId, stacks.Count);
            return ValueTask.CompletedTask;
        }
        if (Check(session, c, targetId, stacks) is { } refusal)
        {
            session.Send(ExpressMessages.Error(refusal.Code, refusal.Item));
            return ValueTask.CompletedTask;
        }
        session.PetJobs.Express = new ExpressJob(petId, targetId, stacks, GameClock.UtcNow.AddSeconds(Math.Max(0, options.Value.ItemUse.ExpressSeconds)));
        session.Send(ExpressMessages.State(petId, started: true));
        session.Send(ExpressMessages.PetWalk(petId, 1));
        logger.LogInformation(
            "[{Session}] {Name}: pet {Pet} sets off to deliver {Count} stack(s) to {Target}",
            session.Id,
            c.Name,
            pet.Name,
            stacks.Count,
            targetId);
        return ValueTask.CompletedTask;
    }

    /// <summary>Ends the sender's trip early, without delivering.</summary>
    /// <param name="session">The sender's session.</param>
    /// <param name="body">The packet body, which is not read.</param>
    internal static ValueTask OnStop(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.PetJobs.Express is { } job)
            Finish(session, job);
        return ValueTask.CompletedTask;
    }

    /// <summary>Called every second: when the trip is due, checks the stacks again, delivers them and ends the trip.</summary>
    /// <param name="session">The sender's session.</param>
    public void Tick(GameSession session)
    {
        if (session.PetJobs.Express is not { } job || job.Due > GameClock.UtcNow || session.ActiveCharacter is not { } c)
            return;
        var refusal = Check(session, c, job.TargetId, job.Stacks);
        if (refusal is null && world.SessionOf(job.TargetId) is { } target && target.ActiveCharacter is { } tc)
        {
            if (!target.TryExclusive(out var held)) // the target is handling a packet: deliver on the next tick
                return;
            using (held)
                Deliver(session, c, target, tc, job);
        }
        else if (refusal is { } r)
            session.Send(ExpressMessages.Error(r.Code, r.Item));
        Finish(session, job);
    }

    /// <summary>Clears the sender's trip and tells the client the pet is back.</summary>
    /// <param name="session">The sender's session.</param>
    /// <param name="job">The trip that ends.</param>
    private static void Finish(GameSession session, ExpressJob job)
    {
        session.PetJobs.Express = null;
        session.Send(ExpressMessages.State(job.PetId, started: false));
        session.Send(ExpressMessages.PetWalk(job.PetId, 3));
    }

    /// <summary>
    /// Checks that a trip can go: the stacks exist, the target is another online player who does not block the sender,
    /// and the target's bag takes them.
    /// </summary>
    /// <param name="session">The sender's session.</param>
    /// <param name="c">The sender's character.</param>
    /// <param name="targetId">The target's login id.</param>
    /// <param name="stacks">The bag slots and counts to carry.</param>
    /// <returns>
    /// The express error code and the item it names, or null when the trip can go. Code 1: a stack is missing or too small. 2: the target is not
    /// online, or is the sender. 3: the target's bag cannot take the stacks. 7: the target is in a minigame. 8: the target blocks the sender.
    /// </returns>
    private (byte Code, ushort Item)? Check(GameSession session, Character c, int targetId, List<(byte Slot, byte Count)> stacks)
    {
        if (stacks.Select(s => s.Slot).Distinct().Count() != stacks.Count) // each bag slot at most once: two lines of one stack would send it twice
            return (1, 0);
        foreach (var (slot, count) in stacks)
        {
            var stack = c.Bag.Find(b => b.Slot == slot);
            if (stack is null || count == 0 || stack.Count < count || Bag.IsNoTrade(items, stack) || session.Vehicle?.BagSlot == slot)
                return (1, stack?.ItemId ?? 0);
        }
        if (world.SessionOf(targetId) is not { } target || target.ActiveCharacter is not { } tc || targetId == session.Account!.LoginId)
            return (2, 0);
        if (BlocklistHandlers.Blocks(tc, session.Account!.LoginId))
            return (8, 0);
        if (target.Minigame is not null)
            return (7, 0);
        var trial = tc.Bag.Select(b => new BagItem { Slot = b.Slot, ItemId = b.ItemId, Count = b.Count, Wear = b.Wear }).ToList();
        foreach (var (slot, count) in stacks)
        {
            var stack = c.Bag.Find(b => b.Slot == slot)!;
            if (Bag.Add(trial, items, stack.ItemId, count, stack.Wear).Sum(x => x.Added) != count)
                return (3, 0);
        }
        return null;
    }

    /// <summary>Moves the stacks from the sender's bag to the target's bag and sends both players the result.</summary>
    /// <param name="session">The sender's session.</param>
    /// <param name="c">The sender's character.</param>
    /// <param name="target">The target's session.</param>
    /// <param name="tc">The target's character.</param>
    /// <param name="job">The trip that delivers.</param>
    private void Deliver(
        GameSession session,
        Character c,
        GameSession target,
        Character tc,
        ExpressJob job)
    {
        var myId = session.Account!.LoginId;
        foreach (var (slot, count) in job.Stacks)
        {
            var stack = c.Bag.Find(b => b.Slot == slot)!;
            var (itemId, wear, insert, fusion, pose, box) = (stack.ItemId, stack.Wear, stack.Insert, stack.Fusion, stack.Pose, stack.BoxItem);
            stack.Count -= count;
            session.Send(ItemMessages.BagSlotDecrease(slot, count));
            if (stack.Count == 0)
                c.Bag.Remove(stack);
            foreach (var (added, n) in Bag.Add(tc, items, itemId, count, wear, insert, fusion, pose, box))
                target.Send(
                    ItemMessages.BagAdd(
                        new BagEntry
                        {
                            Slot = added.Slot,
                            ItemId = added.ItemId,
                            Count = n,
                            Wear = added.Wear,
                            Insert = added.Insert,
                            FusionLevel = added.Fusion,
                            PoseItemId = added.Pose,
                            BoxItemId = added.BoxItem
                        }));
            target.Send(ExpressMessages.Result(1, myId, itemId, count));
            session.Send(ExpressMessages.Result(2, job.TargetId, itemId, count));
        }
        logger.LogInformation("[{Session}] {Name}'s pet delivers {Count} stack(s) to {Target}", session.Id, c.Name, job.Stacks.Count, tc.Name);
    }
}
