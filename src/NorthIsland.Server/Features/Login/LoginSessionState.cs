namespace NorthIsland.Server.Features.Login;

/// <summary>Per-session state of the character select and creation stages.</summary>
public sealed class LoginSessionState
{
    /// <summary>The slot chosen on the character select screen.</summary>
    public byte SelectedSlot { get; set; }

    /// <summary>The name accepted by the last name check, or null when none is pending.</summary>
    public string? PendingName { get; set; }

    /// <summary>
    /// The logins this connection got refused; it is closed once they reach <see cref="ServerOptions.LoginFailuresPerConnection"/>.
    /// </summary>
    public int FailedLogins { get; set; }
}
