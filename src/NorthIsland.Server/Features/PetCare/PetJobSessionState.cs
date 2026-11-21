namespace NorthIsland.Server.Features.PetCare;

/// <summary>The pet jobs and the open reborn wish kept on a player's session.</summary>
public sealed class PetJobSessionState
{
    /// <summary>The pet search in progress; null when none.</summary>
    public ForageJob? Forage { get; set; }

    /// <summary>The pet express trip in progress; null when none.</summary>
    public ExpressJob? Express { get; set; }

    /// <summary>The map on which an NPC's event opened the reborn wish, until the player makes a wish; null when it is not open.</summary>
    public int? RebornWishMap { get; set; }
}
