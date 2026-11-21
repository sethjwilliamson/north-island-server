using System.Collections.Concurrent;

namespace NorthIsland.Server.Features.Login;

/// <summary>
/// The one live session of each account, claimed at a successful login so that a second login (even one that only reached character select)
/// replaces the first instead of sharing its characters.
/// </summary>
public sealed class AccountSessions
{
    /// <summary>The session that last logged in to each account, by login id.</summary>
    private readonly ConcurrentDictionary<int, GameSession> _byLoginId = new();

    /// <summary>
    /// Makes a session the live session of its account and gives back the session it replaces; a replaced session counts only while it is still
    /// logged in to the same account (it has not gone back to the login form or logged in elsewhere).
    /// </summary>
    /// <param name="account">The account the session logged in to.</param>
    /// <param name="session">The session that logged in.</param>
    /// <returns>The earlier live session of the account, or null when there is none.</returns>
    public GameSession? Claim(Account account, GameSession session)
    {
        GameSession? previous = null;
        _byLoginId.AddOrUpdate(
            account.LoginId,
            _ =>
            {
                previous = null;
                return session;
            },
            (_, old) =>
            {
                previous = old;
                return session;
            });
        return previous is not null && !ReferenceEquals(previous, session) && ReferenceEquals(previous.Account, account) ? previous : null;
    }

    /// <summary>Whether a session is the live session of the account it is logged in to.</summary>
    /// <param name="session">The session to check.</param>
    /// <returns>True when the session is logged in and no later login of its account replaced it.</returns>
    public bool Holds(GameSession session) =>
        session.Account is { } account && _byLoginId.TryGetValue(account.LoginId, out var live) && ReferenceEquals(live, session);

    /// <summary>Gives the live session of an account, whether it is in the world or still at character select.</summary>
    /// <param name="loginId">The account's login id.</param>
    /// <returns>The session that holds the account and is still logged in to it, or null when there is none.</returns>
    public GameSession? SessionOf(int loginId) =>
        _byLoginId.TryGetValue(loginId, out var live) && live.Account?.LoginId == loginId ? live : null;

    /// <summary>Gives up the claim of a session on its account; does nothing when a later session holds it.</summary>
    /// <param name="session">The session that leaves.</param>
    public void Release(GameSession session)
    {
        if (session.Account is { } account)
            _byLoginId.TryRemove(new KeyValuePair<int, GameSession>(account.LoginId, session));
    }
}
