namespace NorthIsland.Server.Features.Social;

/// <summary>Settings of the social feature.</summary>
public sealed class SocialOptions
{
    /// <summary>Settings of the puppet training.</summary>
    public PuppetOptions Puppet { get; set; } = new();

    /// <summary>Settings of the HP and SP recovery while sitting.</summary>
    public SittingOptions Sitting { get; set; } = new();
}
