namespace NorthIsland.Server.Features.Social;

/// <summary>Whether a session sits on the ground, and where it sat down, for the HP and SP recovery.</summary>
public sealed class SittingSessionState
{
    /// <summary>True while the player sits.</summary>
    public bool Active { get; set; }

    /// <summary>The map the player sat down on.</summary>
    public ushort MapId { get; set; }

    /// <summary>The x position the player sat down at.</summary>
    public ushort X { get; set; }

    /// <summary>The y position the player sat down at.</summary>
    public ushort Y { get; set; }

    /// <summary>When the next recovery step is due.</summary>
    public DateTime Next { get; set; }
}
