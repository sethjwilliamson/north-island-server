namespace NorthIsland.Server.Features.Social;

/// <summary>Settings of the social feature.</summary>
public sealed class SocialOptions
{
    /// <summary>Settings of the puppet training.</summary>
    public PuppetOptions Puppet { get; set; } = new();

    /// <summary>Settings of the HP and SP recovery while sitting.</summary>
    public SittingOptions Sitting { get; set; } = new();

    /// <summary>
    /// Whether a player can give up a quest going on (24/2). Off by default: the game's quest window never shows its Delete button, so the
    /// normal client never sends it, and giving up a quest lets its steps, and what they hand out, be done again.
    /// </summary>
    public bool QuestAbandon { get; set; }
}
