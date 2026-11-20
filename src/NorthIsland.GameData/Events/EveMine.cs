namespace NorthIsland.GameData.Events;

/// <summary>
/// An encounter zone (地雷塊, "landmine block" in the map editor): a tile rectangle whose events hold the random fights of that part of the map.
/// It is not a gathering spot; those are clicked objects.
/// </summary>
public sealed record EveMine : EvePlacedObject
{
    /// <summary>The x of the opposite rectangle corner (the other corner is <c>X</c>).</summary>
    public int X2 { get; init; }

    /// <summary>The y of the opposite rectangle corner (the other corner is <c>Y</c>).</summary>
    public int Y2 { get; init; }

    /// <summary>The zone kind byte as stored (1 or 2; 2 is mostly used for small zones); its meaning is not established.</summary>
    public byte Kind { get; init; }

    /// <summary>
    /// Byte read at offset 73 of the original record (name taken from that offset), from 1 to 95 in the data and most often 33, 60 or 40; it may be the
    /// zone's encounter chance, but that is not established and nothing here uses it.
    /// </summary>
    public byte Unknown73 { get; init; }

    /// <summary>The rectangle in map pixels.</summary>
    public EvePixelRect Area => EvePixelRect.FromTiles(X, Y, X2, Y2);
}
