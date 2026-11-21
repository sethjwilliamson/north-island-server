namespace NorthIsland.Server.Features.Guilds;

/// <summary>The rules for guilds: the dismissal checks, how often they run, the guild limit and the badge upload wait.</summary>
public sealed class GuildOptions
{
    /// <summary>Whether guilds that miss the checks are dismissed.</summary>
    public bool AutoDismiss { get; set; } = true;

    /// <summary>Days after founding in which the guild must recruit enough members.</summary>
    public int RecruitDays { get; set; } = 7;

    /// <summary>How many members of <see cref="RecruitLevel"/> the guild needs after <see cref="RecruitDays"/>.</summary>
    public int RecruitMembers { get; set; } = 15;

    /// <summary>The level the recruited members need.</summary>
    public int RecruitLevel { get; set; } = 10;

    /// <summary>Days after founding in which enough members must be online together once.</summary>
    public int OnlineDays { get; set; } = 15;

    /// <summary>How many members must be online together once.</summary>
    public int OnlineMembers { get; set; } = 3;

    /// <summary>Minutes between two dismissal checks; 0 or less switches the check off.</summary>
    public int SweepMinutes { get; set; } = 10;

    /// <summary>The most guilds the server holds; 0 means no limit.</summary>
    public int MaxGuilds { get; set; }

    /// <summary>Seconds a guild waits between two badge uploads.</summary>
    public int BadgeUploadSeconds { get; set; } = 60;
}
