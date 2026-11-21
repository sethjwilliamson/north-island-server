namespace NorthIsland.Server.Features.Trading;

/// <summary>One side of a trade: who it is and what they offer.</summary>
/// <param name="session">The session of this side.</param>
public sealed class TradeSide(
    GameSession session)
{
    /// <summary>The session of this side.</summary>
    public GameSession Session { get; } = session;

    /// <summary>The gold offered.</summary>
    public int Gold { get; set; }

    /// <summary>
    /// Copies of the bag stacks offered, taken when the offer was made: each keeps its bag slot, count and every attribute, so the trade can check
    /// the slot still holds the very stack the partner was shown.
    /// </summary>
    public List<BagItem> Stacks { get; } = [];

    /// <summary>The pet offered, or null for none; the trade checks this same pet is still in the team.</summary>
    public Pet? Pet { get; set; }

    /// <summary>Whether an offer has been made.</summary>
    public bool Offered { get; set; }

    /// <summary>Whether this side has confirmed the current offers; an offer from either side after that cancels the trade.</summary>
    public bool Confirmed { get; set; }
}
