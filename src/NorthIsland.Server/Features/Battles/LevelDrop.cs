namespace NorthIsland.Server.Features.Battles;

/// <summary>A row of the level drop table: an item that any monster within a level range may drop, whatever its own item list.</summary>
public sealed class LevelDrop
{
    /// <summary>The item id that drops.</summary>
    public ushort Item { get; set; }

    /// <summary>The lowest monster level the row applies to.</summary>
    public int MinLevel { get; set; }

    /// <summary>The highest monster level the row applies to; 0 means no upper bound.</summary>
    public int MaxLevel { get; set; }

    /// <summary>The chance in percent that a monster in the range drops the item.</summary>
    public double Percent { get; set; }

    /// <summary>Tells whether the row applies to a monster of a level.</summary>
    /// <param name="level">The monster's level.</param>
    /// <returns>True when the level is within the row's range.</returns>
    public bool Covers(int level) => level >= MinLevel && (MaxLevel <= 0 || level <= MaxLevel);
}
