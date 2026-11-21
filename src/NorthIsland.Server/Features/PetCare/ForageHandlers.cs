using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NorthIsland.Protocol.Messages.Items;

namespace NorthIsland.Server.Features.PetCare;

/// <summary>Runs the Dog Bone: a pet looks for items of one kind on the ground of the player's map and brings them back.</summary>
/// <param name="items">The item data, for stacking and names.</param>
/// <param name="ground">The items on the ground.</param>
/// <param name="options">The item options, which hold the trip time and the limit per trip.</param>
/// <param name="logger">The logger.</param>
public sealed class ForageHandlers(
    ItemData items,
    GroundItems ground,
    IOptions<ItemOptions> options,
    ILogger<ForageHandlers> logger)
{
    /// <summary>Item id of the Dog Bone.</summary>
    public const ushort DogBone = 34017; // Dog Bone

    /// <summary>
    /// Starts a search with a Dog Bone after checking the bone, the pet and the wanted item;
    /// a refused request is answered with a failed end.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the bag slot of the bone, the pet's slot, then the wanted item id.</param>
    internal ValueTask OnStart(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || session.ActiveCharacter is not { } c || session.Battle is not null || body.Length < 4)
            return ValueTask.CompletedTask;
        var (boneSlot, petSlot) = (body.Span[0], body.Span[1]);
        var itemId = BinaryPrimitives.ReadUInt16LittleEndian(body.Span[2..]);
        var pet = c.Pets.Find(p => p.Slot == petSlot);
        var record = items[itemId];
        if (c.Bag.Find(b => b.Slot == boneSlot)?.ItemId != DogBone || pet is null || record is null || !Bag.IsStackable(record) || (record.Flags & 2) != 0
            || c.ActivePetId == pet.NpcId || session.RidingPetSlot == petSlot || session.Stalls.Pets is not null || session.PetJobs.Express?.PetId == pet.NpcId
            || (session.PetJobs.Forage is { } running && running.PetSlot != petSlot))
        {
            logger.LogInformation("[{Session}] {Name}: dog bone refused (bone {Bone}, pet {Pet}, item {Item})", session.Id, c.Name, boneSlot, petSlot, itemId);
            session.Send(ForageMessages.End(boneSlot, petSlot, failed: true));
            return ValueTask.CompletedTask;
        }
        session.PetJobs.Forage = new ForageJob(boneSlot, petSlot, itemId, GameClock.UtcNow.AddSeconds(Math.Max(0, options.Value.ItemUse.ForageSeconds)));
        session.Send(ForageMessages.Bone(boneSlot));
        logger.LogInformation("[{Session}] {Name}: {Pet} looks for {Item}", session.Id, c.Name, pet.Name, record.Name.Trim());
        return ValueTask.CompletedTask;
    }

    /// <summary>Cancels the player's search.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body, which is not read.</param>
    internal static ValueTask OnCancel(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.PetJobs.Forage is { } job)
        {
            session.PetJobs.Forage = null;
            session.Send(ForageMessages.End(job.BoneSlot, job.PetSlot, failed: false));
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Called every second: when the search is due, takes the wanted items from the ground into the bag and reports what was found. An index
    /// found holding the wanted item is taken only while it still holds that item, not another thing dropped into the freed index, and never
    /// when another player dropped it as an item only they may pick up. Each stack keeps every attribute it had on the ground.
    /// </summary>
    /// <param name="session">The player's session.</param>
    public void Tick(GameSession session)
    {
        if (session.PetJobs.Forage is not { } job || job.Due > GameClock.UtcNow || session.ActiveCharacter is not { } c)
            return;
        session.PetJobs.Forage = null;
        var found = 0;
        var me = session.Account?.LoginId ?? 0;
        foreach (var index in ground.Find(c.MapId, job.ItemId).Take(options.Value.ItemUse.ForageMaxPerTrip))
        {
            if (Bag.FreeSlot(c, items, job.ItemId) == 0 && !c.Bag.Exists(b => b.ItemId == job.ItemId && b.Count < Bag.MaxStack))
                break;
            var thing = ground.TakeIf(
                session,
                index,
                (t, _, _) => t.ItemId != job.ItemId || t.Gold != 0 ? "another thing lies there now"
                    : t.Owner != 0 && t.Owner != me ? "only the player who dropped it may pick it up"
                        : null,
                out var refused);
            if (thing is null)
            {
                if (refused is not null)
                    logger.LogInformation("[{Session}] {Name}'s pet leaves ground index {Index}: {Why}", session.Id, c.Name, index, refused);
                continue;
            }
            var count = Math.Max(1, thing.Count);
            var added = Bag.Add(c, items, thing.ItemId, count, thing.Wear, thing.Insert, thing.Fusion, thing.Pose, thing.BoxItem, thing.Used, thing.Extra);
            Bag.SendAdded(session, added); // 23/8 names each slot with the stack's attributes
            found += count;
        }
        session.Send(found > 0 ? ForageMessages.Found(job.ItemId, (byte)Math.Min(255, found)) : ForageMessages.End(job.BoneSlot, job.PetSlot, failed: true));
        logger.LogInformation("[{Session}] {Name}'s pet brings back {Count} of {Item}", session.Id, c.Name, found, job.ItemId);
    }
}
