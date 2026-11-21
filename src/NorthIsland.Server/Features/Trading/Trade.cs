namespace NorthIsland.Server.Features.Trading;

/// <summary>A trade in progress between two sessions, shared by the item trade, the large-item trade and the pet trade.</summary>
/// <param name="first">The session that asked for the trade.</param>
/// <param name="second">The session that was asked.</param>
public sealed class Trade(
    GameSession first,
    GameSession second)
{

    /// <summary>Guards the sides while offers change or the trade is carried out.</summary>
    public Lock Lock { get; } = new();

    /// <summary>The side of the session that asked for the trade.</summary>
    public TradeSide First { get; } = new(first);

    /// <summary>The side of the session that was asked.</summary>
    public TradeSide Second { get; } = new(second);

    /// <summary>Whether the trade has ended and must not change any more.</summary>
    public bool Closed { get; set; }

    /// <summary>
    /// Whether a confirmation found both sides confirmed and that packet carries the trade out; a later confirmation leaves it alone, so that
    /// only one of the two players' packets takes the other's session gate.
    /// </summary>
    public bool Settling { get; set; }

    /// <summary>Whether this is a large-item trade, which uses its own packets.</summary>
    public bool Large { get; init; }

    /// <summary>The side whose bag had no room when the trade failed with too many items, or null.</summary>
    public TradeSide? NoSpace { get; set; }

    /// <summary>Returns the side of a session.</summary>
    /// <param name="session">The player's session.</param>
    /// <returns>The side of the session.</returns>
    public TradeSide Of(GameSession session) => ReferenceEquals(First.Session, session) ? First : Second;

    /// <summary>Returns the side of the other player.</summary>
    /// <param name="session">The player's session.</param>
    /// <returns>The side of the partner of the session.</returns>
    public TradeSide PartnerOf(GameSession session) => ReferenceEquals(First.Session, session) ? Second : First;
}
