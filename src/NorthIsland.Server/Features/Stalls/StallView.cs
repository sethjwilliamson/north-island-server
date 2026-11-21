namespace NorthIsland.Server.Features.Stalls;

/// <summary>What a buyer was last shown of a stall: the stall itself and the price of each slot on the list they got.</summary>
/// <param name="Stall">The stall that was shown; a stall closed and opened again is another object.</param>
/// <param name="Prices">The price shown for each slot, by stall slot.</param>
public sealed record StallView(
    object Stall,
    IReadOnlyDictionary<byte, int> Prices)
{
    /// <summary>Tells whether the buyer was shown a slot of this very stall at this price.</summary>
    /// <param name="view">The buyer's view, or null when they were shown none.</param>
    /// <param name="stall">The stall bought from.</param>
    /// <param name="slot">The stall slot bought.</param>
    /// <param name="price">The slot's current price.</param>
    /// <returns>True when the buyer saw that price on this stall.</returns>
    public static bool Saw(StallView? view, object stall, byte slot, int price) =>
        view is not null && ReferenceEquals(view.Stall, stall) && view.Prices.TryGetValue(slot, out var seen) && seen == price;
}
