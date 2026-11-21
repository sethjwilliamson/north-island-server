namespace NorthIsland.Server.Features.Social;

/// <summary>Per-session state of the friend feature.</summary>
public sealed class FriendSessionState
{
    /// <summary>Login id of the player whose friend request is waiting for this player's answer; 0 when none.</summary>
    public int PendingFrom { get; set; }

    /// <summary>When this session last sent a mail (UTC); <see cref="DateTime.MinValue"/> when it has sent none.</summary>
    public DateTime LastMailUtc { get; set; } = DateTime.MinValue;
}
