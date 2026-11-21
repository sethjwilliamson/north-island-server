namespace NorthIsland.Server.Features.Stalls;

/// <summary>A stall of large items and vehicles opened by a player.</summary>
public sealed class UtensilStall
{
    /// <summary>The stall's name.</summary>
    public required string Name { get; init; }

    /// <summary>The sign style byte the player chose.</summary>
    public byte Sign { get; init; }

    /// <summary>The price of each offered bag stack, by bag slot.</summary>
    public Dictionary<byte, int> Prices { get; } = [];

    /// <summary>
    /// A copy of each offered bag stack as it was when the stall opened, by bag slot, its count lowered by each sale; a buy needs the slot to still
    /// hold this stack.
    /// </summary>
    public Dictionary<byte, BagItem> Offered { get; } = [];
}
