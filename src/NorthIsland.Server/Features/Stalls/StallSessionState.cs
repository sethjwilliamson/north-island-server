namespace NorthIsland.Server.Features.Stalls;

/// <summary>Per-session state of the stalls: which stall the player has open.</summary>
public sealed class StallSessionState
{
    /// <summary>The player's item stall; null when none.</summary>
    public ItemStall? Items { get; set; }

    /// <summary>The player's pet stall; null when none.</summary>
    public PetStall? Pets { get; set; }

    /// <summary>The player's large item stall; null when none.</summary>
    public UtensilStall? Utensils { get; set; }

    /// <summary>The item stall this player was last shown as a buyer, with the prices on that list; null when none.</summary>
    public StallView? SeenItems { get; set; }

    /// <summary>The pet stall this player was last shown as a buyer, with the prices on that list; null when none.</summary>
    public StallView? SeenPets { get; set; }

    /// <summary>The large item stall this player was last shown as a buyer, with the prices on that list; null when none.</summary>
    public StallView? SeenUtensils { get; set; }

    /// <summary>Whether the player has any stall open.</summary>
    public bool Any => Items is not null || Pets is not null || Utensils is not null;
}
