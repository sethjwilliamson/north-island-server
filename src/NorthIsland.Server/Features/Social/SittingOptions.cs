namespace NorthIsland.Server.Features.Social;

/// <summary>
/// Settings of the HP and SP a player, and their active pet, get back while sitting on the ground (the Social window's "sit down"
/// action, Alt+1 by default). The defaults are guesses from forum posts; the original client only says "Press Alt+1 to recover HP and SP gradually".
/// </summary>
public sealed class SittingOptions
{
    /// <summary>Switches the recovery on or off.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Seconds between two recovery steps.</summary>
    public int TickSeconds { get; set; } = 5;

    /// <summary>The HP one step gives back; 0 for none.</summary>
    public int HpPerTick { get; set; } = 6;

    /// <summary>The SP one step gives back; 0 for none.</summary>
    public int SpPerTick { get; set; } = 3;

    /// <summary>True to give the active pet the same step.</summary>
    public bool ActivePet { get; set; } = true;
}
