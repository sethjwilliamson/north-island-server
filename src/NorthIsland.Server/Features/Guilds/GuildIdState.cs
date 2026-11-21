namespace NorthIsland.Server.Features.Guilds;

/// <summary>The saved guild id counter, so that the id of a removed guild is never given to a new one.</summary>
public sealed class GuildIdState
{
    /// <summary>The id given to the last founded guild; the next guild gets one more.</summary>
    public uint LastId { get; set; }
}
