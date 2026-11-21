namespace NorthIsland.Server.Features.Progression;

/// <summary>Per-session state of the career form.</summary>
public sealed class CareerSessionState
{
    /// <summary>The map on which an NPC's event opened the career form, until the player chooses a job; null when it is not open.</summary>
    public int? FormMap { get; set; }
}
