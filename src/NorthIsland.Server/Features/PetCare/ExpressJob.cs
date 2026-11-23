namespace NorthIsland.Server.Features.PetCare;

/// <summary>A pet express trip in progress.</summary>
/// <param name="PetId">The NPC id of the pet that carries.</param>
/// <param name="TargetId">The login id of the receiving player.</param>
/// <param name="Stacks">The bag slots and counts carried.</param>
/// <param name="Due">When the pet arrives.</param>
/// <param name="Loaded">
/// What each carried slot held when the trip started, in the order of <paramref name="Stacks"/>: the trip delivers exactly those stacks, or
/// nothing, even when a handler that does not ask <see cref="BagLocks"/> emptied a slot and something else filled it meanwhile.
/// </param>
public sealed record ExpressJob(
    int PetId,
    int TargetId,
    List<(byte Slot, byte Count)> Stacks,
    DateTime Due,
    List<ExpressLoad> Loaded)
{
    /// <summary>Finds the first carried slot that no longer holds the stack loaded at the start of the trip.</summary>
    /// <param name="bag">The sender's bag now.</param>
    /// <returns>The slot that changed and the item it holds now (0 for none), or null when every slot still holds its loaded stack.</returns>
    public (byte Slot, ushort Item)? Changed(IReadOnlyList<BagItem> bag)
    {
        foreach (var load in Loaded)
        {
            var now = bag.FirstOrDefault(b => b.Slot == load.Slot);
            if (!load.Holds(now))
                return (load.Slot, now?.ItemId ?? 0);
        }
        return null;
    }
}

/// <summary>The item and attributes a bag slot held when an express trip was loaded; the count may only have grown or stayed.</summary>
/// <param name="Slot">The bag slot.</param>
/// <param name="ItemId">The item's id.</param>
/// <param name="Wear">The item's wear value.</param>
/// <param name="Insert">The magic crystal stone set in the item.</param>
/// <param name="Fusion">The fusion level.</param>
/// <param name="Pose">The pose look.</param>
/// <param name="Used">The used amount.</param>
/// <param name="Extra">The extra value.</param>
/// <param name="BoxItem">The box the item came in.</param>
public sealed record ExpressLoad(
    byte Slot,
    ushort ItemId,
    byte Wear,
    ushort Insert,
    byte Fusion,
    ushort Pose,
    int Used,
    ushort Extra,
    ushort BoxItem)
{
    /// <summary>Records what a bag stack is now.</summary>
    /// <param name="stack">The bag stack.</param>
    /// <returns>The stack's item and attributes.</returns>
    public static ExpressLoad Of(BagItem stack) =>
        new(stack.Slot, stack.ItemId, stack.Wear, stack.Insert, stack.Fusion, stack.Pose, stack.Used, stack.Extra, stack.BoxItem);

    /// <summary>Whether a bag stack is still the loaded one: the same slot, item and attributes (the count is checked separately).</summary>
    /// <param name="stack">The stack in the slot now, or null for an empty slot.</param>
    /// <returns>True when it is the same stack.</returns>
    public bool Holds(BagItem? stack) => stack is not null && this == Of(stack);
}
