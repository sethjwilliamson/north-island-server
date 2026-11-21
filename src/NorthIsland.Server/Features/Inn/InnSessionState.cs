namespace NorthIsland.Server.Features.Inn;

/// <summary>The inn state of one session.</summary>
public sealed class InnSessionState
{
    /// <summary>The price of the rest that was offered and not yet answered; null when no rest is on offer.</summary>
    public int? PendingPrice { get; set; }

    /// <summary>
    /// The character the rest was offered to; the offer stands only for this character, on <see cref="OfferMap"/>, like a service window. It does
    /// not depend on the event run that offered it: a click on the world between the offer and the answer ends that run.
    /// </summary>
    public Character? OfferCharacter { get; set; }

    /// <summary>The map id the rest was offered on.</summary>
    public ushort OfferMap { get; set; }
}
