namespace NorthIsland.Server.Features.Guilds;

/// <summary>The saved state of the castle.</summary>
public sealed class CastleState
{
    /// <summary>Id of the guild that owns the castle, 0 for none.</summary>
    public uint OwnerGuildId { get; set; }

    /// <summary>A time value saved with the state; the server does not read or write it.</summary>
    public double Since { get; set; }

    /// <summary>The ISO week (year * 100 + week) in which each account last took the weekly castle reward, by login id.</summary>
    public Dictionary<int, int> RewardWeeks { get; set; } = [];
}
